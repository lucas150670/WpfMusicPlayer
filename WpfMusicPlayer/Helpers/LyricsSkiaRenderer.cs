// SPDX-License-Identifier: MIT

using SkiaSharp;
using Topten.RichTextKit;

namespace WpfMusicPlayer.Helpers;

/// <summary>
/// SkiaSharp + RichTextKit lyric renderer. Owns no surface or bitmap; every draw call
/// paints onto the caller-provided <see cref="SKCanvas"/> (DIP coordinates). Text layout,
/// word wrapping and CJK/emoji font fallback are handled by a cached RichTextKit
/// <see cref="TextBlock"/> per (text, size, weight, width, colour).
/// </summary>
internal sealed class LyricsSkiaRenderer : IDisposable
{
    private const string FontFamilyName = "Segoe UI";
    private const int MaxCachedTextBlocks = 128;

    private readonly record struct TextBlockKey(string Text, int SizeKey, bool Bold, int WidthKey, SKColor Color);

    private readonly Dictionary<TextBlockKey, TextBlock> _textBlocks = [];
    // Geometry-only index into the same cached blocks: text layout is colour-independent,
    // so measurement and karaoke hit-testing can reuse any block with matching
    // (text, size, weight, wrap width) regardless of the colour it was created for.
    private readonly Dictionary<(string Text, int SizeKey, bool Bold, int WidthKey), TextBlock> _geometryBlocks = [];
    private bool _disposed;

    public (float Height, float Width) MeasureText(string text, float fontSize, bool bold, float maxWidth)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var block = GetGeometryBlock(text, fontSize, bold, maxWidth);
        return (
            Math.Max(block.MeasuredHeight, fontSize),
            Math.Max(block.MeasuredWidth, 0f));
    }

    /// <summary>
    /// Computes, for the given progress (0..1) and word-wrapped layout, the per-wrapped-line
    /// highlight widths. Each entry corresponds to one wrapped line and is the horizontal
    /// length (in DIPs, from the line's left edge) that should be coloured as "sung".
    /// Same karaoke math as the original DirectWrite implementation: charProgress =
    /// progress * charCount gives the fully lit characters plus a sub-character partial
    /// fill, mapped onto each wrapped line via caret hit-testing. Indices are code-point
    /// based (the RichTextKit equivalent of the original UTF-16 positions).
    /// </summary>
    public float[] ComputeKaraokeLineWidths(
        string text,
        float fontSize,
        bool bold,
        float maxWidth,
        double progress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        text ??= string.Empty;
        var block = GetGeometryBlock(text, fontSize, bold, maxWidth);
        var lines = block.Lines;
        if (lines.Count == 0)
            return [];

        var length = CountCodePoints(text);
        if (length == 0)
            return [];

        var widths = new float[lines.Count];

        progress = Math.Clamp(progress, 0.0, 1.0);
        if (progress <= 0.0)
            return widths; // all zeros

        // Original karaoke math: progress * charCount => full chars + fractional char.
        var charProgress = progress * length;
        var fullChars = Math.Min((int)charProgress, length);
        var subProgress = Math.Clamp(charProgress - fullChars, 0.0, 1.0);

        for (var line = 0; line < lines.Count; line++)
        {
            var textOffset = lines[line].Start;
            var lineEnd = lines[line].End; // first text position AFTER this line
            var lineLen = lineEnd - textOffset;

            if (fullChars >= lineEnd)
            {
                // Whole line is fully sung. Measure the trailing edge of THIS line's own
                // last character. Querying the line-end position itself would resolve to
                // the first character of the NEXT wrapped line (x near the left edge),
                // collapsing a completed line's highlight to roughly one character width.
                widths[line] = lineLen > 0
                    ? GetTrailingEdgeX(block, lineEnd - 1)
                    : 0f;
            }
            else if (fullChars <= textOffset)
            {
                // Line not started yet, unless the partial character sits exactly on
                // the boundary (first char of this line is partially sung).
                if (fullChars == textOffset && subProgress > 0.001 && lineLen > 0)
                {
                    var startX = GetLeadingEdgeX(block, fullChars);
                    var nextX = GetCharEndX(block, fullChars, lineEnd);
                    widths[line] = startX + (nextX - startX) * (float)subProgress;
                }
                else
                {
                    widths[line] = 0f;
                }
            }
            else
            {
                // Partially sung line: full chars up to fullChars, plus the fractional
                // character at fullChars (if any remains on this line).
                var edgeX = GetLeadingEdgeX(block, fullChars);
                if (subProgress > 0.001 && fullChars < lineEnd)
                {
                    var nextX = GetCharEndX(block, fullChars, lineEnd);
                    edgeX += (nextX - edgeX) * (float)subProgress;
                }

                widths[line] = edgeX;
            }
        }

        return widths;
    }

    /// <summary>
    /// Draws the main lyric text with the karaoke highlight applied. The base text is drawn
    /// in <paramref name="baseColor"/>; the sung portion (per wrapped line widths computed
    /// by <see cref="ComputeKaraokeLineWidths"/>) is redrawn on top in
    /// <paramref name="highlightColor"/> using an aliased axis-aligned clip per line.
    /// </summary>
    public void DrawKaraokeText(
        SKCanvas canvas,
        string text,
        float fontSize,
        bool bold,
        float x,
        float y,
        float maxWidth,
        float maxHeight,
        float scale,
        SKColor baseColor,
        SKColor highlightColor,
        float[] lineWidths)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (lineWidths.Length == 0)
            return;

        scale = Math.Clamp(scale, 0.01f, 1f);
        var layoutHeight = Math.Max(1f, scale < 0.999f ? maxHeight / scale : maxHeight);

        var baseBlock = GetBlock(text, fontSize, bold, maxWidth, baseColor);
        var highlightBlock = GetBlock(text, fontSize, bold, maxWidth, highlightColor);
        baseBlock.MaxHeight = layoutHeight;
        highlightBlock.MaxHeight = layoutHeight;

        var scaled = scale < 0.999f;
        if (scaled)
        {
            canvas.Save();
            canvas.Translate(x, y);
            canvas.Scale(scale);
            canvas.Translate(-x, -y);
        }

        try
        {
            var origin = new SKPoint(x, y);

            // Base (dim) text first.
            baseBlock.Paint(canvas, origin, TextPaintOptions.Default);

            // Highlighted (sung) overlay clipped per wrapped line.
            var lines = baseBlock.Lines;
            var lineCount = Math.Min(lines.Count, lineWidths.Length);
            var lineTop = 0f;
            for (var line = 0; line < lineCount; line++)
            {
                var w = lineWidths[line];
                var lineHeight = lines[line].Height;
                if (w > 0.01f)
                {
                    canvas.Save();
                    canvas.ClipRect(
                        new SKRect(x, y + lineTop, x + w, y + lineTop + lineHeight),
                        SKClipOperation.Intersect,
                        antialias: false);
                    highlightBlock.Paint(canvas, origin, TextPaintOptions.Default);
                    canvas.Restore();
                }

                lineTop += lineHeight;
            }
        }
        finally
        {
            if (scaled)
                canvas.Restore();
        }
    }

    public void DrawText(
        SKCanvas canvas,
        string text,
        float fontSize,
        bool bold,
        float x,
        float y,
        float width,
        float height,
        SKColor color,
        float scale = 1f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        scale = Math.Clamp(scale, 0.01f, 1f);
        var layoutHeight = Math.Max(1f, scale < 0.999f ? height / scale : height);

        var block = GetBlock(text, fontSize, bold, width, color);
        block.MaxHeight = layoutHeight;

        var scaled = scale < 0.999f;
        if (scaled)
        {
            canvas.Save();
            canvas.Translate(x, y);
            canvas.Scale(scale);
            canvas.Translate(-x, -y);
        }

        try
        {
            block.Paint(canvas, new SKPoint(x, y), TextPaintOptions.Default);
        }
        finally
        {
            if (scaled)
                canvas.Restore();
        }
    }

    public void FillRoundedRectangle(
        SKCanvas canvas,
        float x,
        float y,
        float width,
        float height,
        float radius,
        SKColor color)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var paint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawRoundRect(
            new SKRect(x, y, x + Math.Max(0f, width), y + Math.Max(0f, height)),
            radius,
            radius,
            paint);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // TextBlock is not disposable in RichTextKit 0.4.167; dropping the references
        // is all that is required.
        _textBlocks.Clear();
        _geometryBlocks.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Returns the cached layout for measurement/karaoke hit-testing. Geometry is
    /// colour-independent, so any cached block with matching text/format/width is used;
    /// if none exists yet a white one is created (colour never affects wrapping).
    /// </summary>
    private TextBlock GetGeometryBlock(string text, float fontSize, bool bold, float maxWidth)
    {
        text ??= string.Empty;
        maxWidth = Math.Max(1f, maxWidth);
        var key = (text, (int)Math.Round(fontSize * 10f), bold, (int)Math.Round(maxWidth * 10f));
        if (_geometryBlocks.TryGetValue(key, out var block))
        {
            // A paint call may have capped this block's height; measurement and karaoke
            // hit-testing must always see the full, untruncated layout.
            block.MaxHeight = null;
            return block;
        }

        return GetBlock(text, fontSize, bold, maxWidth, SKColors.White);
    }

    private TextBlock GetBlock(string text, float fontSize, bool bold, float maxWidth, SKColor color)
    {
        text ??= string.Empty;
        maxWidth = Math.Max(1f, maxWidth);
        var sizeKey = (int)Math.Round(fontSize * 10f);
        var widthKey = (int)Math.Round(maxWidth * 10f);
        var key = new TextBlockKey(text, sizeKey, bold, widthKey, color);
        if (_textBlocks.TryGetValue(key, out var block))
            return block;

        if (_textBlocks.Count >= MaxCachedTextBlocks)
        {
            _textBlocks.Clear();
            _geometryBlocks.Clear();
        }

        block = new TextBlock
        {
            MaxWidth = maxWidth,
            EllipsisEnabled = false
        };
        block.AddText(text, new Style
        {
            FontFamily = FontFamilyName,
            FontSize = Math.Max(1f, fontSize),
            FontWeight = bold ? 700 : 400,
            TextColor = color
        });
        _textBlocks.Add(key, block);
        _geometryBlocks.TryAdd((text, sizeKey, bold, widthKey), block);
        return block;
    }

    private static int CountCodePoints(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
            count++;
        return count;
    }

    /// <summary>X coordinate (DIP) of the leading edge of the given code point index.</summary>
    private static float GetLeadingEdgeX(TextBlock block, int codePointIndex)
    {
        var info = block.GetCaretInfo(new CaretPosition(codePointIndex, altPosition: false));
        return info.IsNone ? 0f : info.CaretXCoord;
    }

    /// <summary>X coordinate (DIP) of the trailing edge of the given code point index.</summary>
    private static float GetTrailingEdgeX(TextBlock block, int codePointIndex)
    {
        // The caret before the NEXT code point, kept on the current line: altPosition
        // makes a position sitting exactly on a wrap boundary resolve to the end of the
        // previous line instead of the start of the following one.
        var info = block.GetCaretInfo(new CaretPosition(codePointIndex + 1, altPosition: true));
        return info.IsNone ? 0f : info.CaretXCoord;
    }

    /// <summary>
    /// X coordinate (DIP) of the end of the character at <paramref name="charPos"/>, staying
    /// on the same wrapped line: the leading edge of the next character when it still lies
    /// within <paramref name="lineEnd"/>, otherwise the trailing edge of the character
    /// itself. Never follows the next position onto the next wrapped line, whose leading
    /// edge sits at the left edge of the layout (x ≈ 0) and would collapse the width.
    /// </summary>
    private static float GetCharEndX(TextBlock block, int charPos, int lineEnd) =>
        charPos + 1 < lineEnd
            ? GetLeadingEdgeX(block, charPos + 1)
            : GetTrailingEdgeX(block, charPos);
}
