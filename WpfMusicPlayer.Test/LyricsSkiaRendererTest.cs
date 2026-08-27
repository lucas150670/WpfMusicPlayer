// SPDX-License-Identifier: MIT

using SkiaSharp;
using Topten.RichTextKit;
using WpfMusicPlayer.Helpers;

namespace WpfMusicPlayer.Test;

[TestClass]
public sealed class LyricsSkiaRendererTest
{
    [TestMethod]
    public void RichTextKit_LayoutAndPaint_WorksAgainstReferencedSkiaSharp()
    {
        // Compatibility canary: Topten.RichTextKit 0.4.167 was compiled against
        // SkiaSharp 2.88. If the referenced SkiaSharp dropped the legacy SKPaint text
        // APIs that RichTextKit still calls, layout or paint throws
        // MissingMethodException/TypeLoadException and this test fails.
        using var surface = SKSurface.Create(new SKImageInfo(220, 100));
        var block = new TextBlock { MaxWidth = 200f };
        block.AddText("你好世界 hello", new Style
        {
            FontFamily = "Segoe UI",
            FontSize = 20,
            FontWeight = 700,
            TextColor = SKColors.White
        });

        Assert.IsGreaterThan(0f, block.MeasuredHeight, "layout should produce a positive height");
        block.Paint(surface.Canvas, new SKPoint(0, 0), TextPaintOptions.Default);

        var caret = block.GetCaretInfo(new CaretPosition(2, false));
        Assert.IsFalse(caret.IsNone, "caret hit-testing should resolve inside the laid out text");
    }

    [TestMethod]
    public void DrawText_WritesNonTransparentPixels()
    {
        using var renderer = new LyricsSkiaRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(160, 80));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        renderer.DrawText(canvas, "ABC", 28, true, 0, 16, 160, 48, SKColors.White);

        var pixels = ReadPixels(surface, 160, 80);
        var sawAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                sawAlpha = true;
                break;
            }
        }

        Assert.IsTrue(sawAlpha, "Drawn text should write non-zero alpha into the surface.");
    }

    [TestMethod]
    public void ComputeKaraokeLineWidths_ZeroProgress_YieldsAllZeros()
    {
        using var renderer = new LyricsSkiaRenderer();
        var widths = renderer.ComputeKaraokeLineWidths("你好世界 hello world", 24, true, 380, 0.0);
        Assert.IsNotNull(widths);
        Assert.IsTrue(widths.Length > 0);
        foreach (var w in widths)
            Assert.AreEqual(0f, w, 0.0001f);
    }

    [TestMethod]
    public void ComputeKaraokeLineWidths_FullProgress_YieldsPositiveWidths()
    {
        using var renderer = new LyricsSkiaRenderer();
        var widths = renderer.ComputeKaraokeLineWidths("你好世界 hello world", 24, true, 380, 1.0);
        Assert.IsNotNull(widths);
        Assert.IsTrue(widths.Length > 0);
        foreach (var w in widths)
            Assert.IsGreaterThan(0f, w, "fully sung lines must have a positive highlight width");
    }

    [TestMethod]
    public void ComputeKaraokeLineWidths_PartialProgress_IsMonotonicAcrossLines()
    {
        using var renderer = new LyricsSkiaRenderer();
        // Long text forced to wrap into multiple lines at a narrow width.
        const string text = "AAAA BBBB CCCC DDDD EEEE FFFF GGGG HHHH";
        var full = renderer.ComputeKaraokeLineWidths(text, 28, true, 200, 1.0);
        var half = renderer.ComputeKaraokeLineWidths(text, 28, true, 200, 0.5);
        Assert.AreEqual(full.Length, half.Length, "line count must match between passes");

        var lastPositive = -1;
        for (var i = 0; i < half.Length; i++)
        {
            // No line may exceed its fully-sung width.
            Assert.IsLessThanOrEqualTo(full[i] + 0.5f, half[i],
                $"line {i} partial width exceeds full width");
            if (half[i] > 0f)
                lastPositive = i;
        }

        Assert.IsGreaterThanOrEqualTo(0, lastPositive, "half progress should highlight something");
        // Lines after the last positive one must be untouched (0).
        for (var i = lastPositive + 1; i < half.Length; i++)
            Assert.AreEqual(0f, half[i], 0.0001f, $"line {i} after the sung position must stay 0");
    }

    [TestMethod]
    public void ComputeKaraokeLineWidths_ProgressAdvance_IsMonotonicallyNonDecreasing()
    {
        using var renderer = new LyricsSkiaRenderer();
        const string text = "AAAA BBBB CCCC DDDD EEEE FFFF GGGG HHHH";
        var previous = renderer.ComputeKaraokeLineWidths(text, 28, true, 200, 0.0);
        for (var progress = 0.05; progress <= 1.001; progress += 0.05)
        {
            var current = renderer.ComputeKaraokeLineWidths(text, 28, true, 200, progress);
            Assert.AreEqual(previous.Length, current.Length, "line count must stay stable across progress");
            for (var i = 0; i < current.Length; i++)
            {
                Assert.IsLessThanOrEqualTo(0.01f, previous[i] - current[i],
                    $"line {i} width decreased when progress advanced to {progress:F2}");
            }

            previous = current;
        }
    }

    [TestMethod]
    public void ComputeKaraokeLineWidths_FullySungWrappedLines_KeepFullLineWidth()
    {
        using var renderer = new LyricsSkiaRenderer();
        // Long text forced to wrap into multiple lines at a narrow width.
        const string text = "AAAA BBBB CCCC DDDD EEEE FFFF GGGG HHHH";
        var (_, charWidth) = renderer.MeasureText("A", 28, true, 1000);
        Assert.IsGreaterThan(0f, charWidth);

        var full = renderer.ComputeKaraokeLineWidths(text, 28, true, 200, 1.0);
        Assert.IsTrue(full.Length > 1, "test text must wrap into multiple lines");

        // Regression: once karaoke progress moves past a wrapped line, that line must
        // keep (approximately) its full width. Measuring the first character of the
        // NEXT line instead collapsed the highlight to roughly one character width.
        for (var i = 0; i < full.Length; i++)
        {
            Assert.IsGreaterThan(charWidth * 2, full[i],
                $"fully sung line {i} collapsed to roughly one character width");
        }
    }

    [TestMethod]
    public void DrawKaraokeText_WritesBaseAndHighlightPixels()
    {
        using var renderer = new LyricsSkiaRenderer();
        const string text = "你好世界 hello";
        var baseColor = new SKColor(0xDD, 0xDD, 0xDD, 0x88);
        var highlightColor = SKColors.White;
        var widths = renderer.ComputeKaraokeLineWidths(text, 28, true, 300, 0.5);

        using var surface = SKSurface.Create(new SKImageInfo(320, 160));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        renderer.DrawKaraokeText(
            canvas,
            text, 28, true, 0, 8, 300, 120, 1f,
            baseColor,
            highlightColor,
            widths);

        var pixels = ReadPixels(surface, 320, 160);
        var sawBase = false;
        var sawHighlight = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 0)
                continue;

            // BGRA; the base colour is a dim grey, the highlight is opaque white.
            var red = pixels[i + 2];
            if (red > 200 && alpha > 200)
                sawHighlight = true;
            else if (red < 200)
                sawBase = true;

            if (sawBase && sawHighlight)
                break;
        }

        Assert.IsTrue(sawBase, "Karaoke text should paint the dim base colour.");
        Assert.IsTrue(sawHighlight, "Karaoke text should paint the sung highlight colour.");
    }

    private static byte[] ReadPixels(SKSurface surface, int width, int height)
    {
        using var image = surface.Snapshot();
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        Assert.IsTrue(
            image.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0),
            "snapshot pixels should be readable");
        return bitmap.Bytes;
    }
}
