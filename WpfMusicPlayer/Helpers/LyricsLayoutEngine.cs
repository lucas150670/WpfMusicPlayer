// SPDX-License-Identifier: MIT

namespace WpfMusicPlayer.Helpers;

public static class LyricsLayoutEngine
{
    public const float ItemPaddingY = 13f;
    public const float SecondaryLineGap = 2f;
    public const float ContentPaddingLeft = 16f;
    public const float ContentPaddingRight = 20f;
    public const float SecondaryFontSize = 15f;
    // Every lyric line is drawn at the same size; the active line stands out through
    // colour alone. The main text is 1.8x the secondary (translation/romanji) size.
    public const float MainFontSize = SecondaryFontSize * 1.8f;
    public const double HoverFadeSeconds = 0.18;
    // Duration of one line's scroll ease, plus the extra start delay added per row
    // below the active line: lower rows join the upward motion later, so the list
    // appears to flow from top to bottom.
    public const double ScrollAnimationMilliseconds = 500;
    public const double ScrollStaggerPerLineMilliseconds = 60;
    public const int ScrollStaggerMaxLines = 6;
    // Fraction of the viewport height where the active line's top edge rests while
    // auto-following, so upcoming lines stay visible below it.
    public const double ActiveLineAnchorRatio = 0.2;
    public static readonly TimeSpan AutoFollowIdleThreshold = TimeSpan.FromSeconds(10);

    public static bool ShouldResumeAutoFollow(bool followSuspended, TimeSpan idleDuration) =>
        followSuspended && idleDuration >= AutoFollowIdleThreshold;

    public static float EaseInOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t < 0.5f
            ? 4f * t * t * t
            : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;
    }

    public static double StaggerDelayMilliseconds(int lineIndex, int anchorIndex)
    {
        var rowsBelow = Math.Clamp(lineIndex - anchorIndex, 0, ScrollStaggerMaxLines);
        return rowsBelow * ScrollStaggerPerLineMilliseconds;
    }

    public readonly struct LyricSizeMetrics
    {
        public LyricSizeMetrics(
            float textHeight,
            float translationHeight,
            float romanjiHeight,
            float textWidth = 0f,
            float translationWidth = 0f,
            float romanjiWidth = 0f)
        {
            TextHeight = Math.Max(0f, textHeight);
            TranslationHeight = Math.Max(0f, translationHeight);
            RomanjiHeight = Math.Max(0f, romanjiHeight);
            TextWidth = Math.Max(0f, textWidth);
            TranslationWidth = Math.Max(0f, translationWidth);
            RomanjiWidth = Math.Max(0f, romanjiWidth);
            LineHeight = MeasureLineHeight(
                TextHeight,
                TranslationHeight > 0f ? TranslationHeight : null,
                RomanjiHeight > 0f ? RomanjiHeight : null);
        }

        public float TextHeight { get; }
        public float TranslationHeight { get; }
        public float RomanjiHeight { get; }
        public float TextWidth { get; }
        public float TranslationWidth { get; }
        public float RomanjiWidth { get; }
        public float LineHeight { get; }
    }

    public static float MeasureLineHeight(float textHeight, float? translationHeight, float? romanjiHeight)
    {
        var height = ItemPaddingY * 2f + Math.Max(0f, textHeight);
        if (translationHeight is > 0f)
            height += SecondaryLineGap + translationHeight.Value;
        if (romanjiHeight is > 0f)
            height += SecondaryLineGap + romanjiHeight.Value;
        return height;
    }

    public static double ComputeContentHeight(IReadOnlyList<float> lineHeights)
    {
        double total = 0;
        for (var i = 0; i < lineHeights.Count; i++)
            total += lineHeights[i];
        return total;
    }

    public static double ComputeAnchorOffset(
        double itemTop,
        double viewportHeight,
        double contentHeight)
    {
        var target = itemTop - viewportHeight * ActiveLineAnchorRatio;
        var max = Math.Max(0d, contentHeight - viewportHeight);
        return Math.Clamp(target, 0d, max);
    }

    public static double ClampOffset(double offset, double viewportHeight, double contentHeight)
    {
        var max = Math.Max(0d, contentHeight - viewportHeight);
        return Math.Clamp(offset, 0d, max);
    }

    public static int HitTestLine(IReadOnlyList<float> lineTops, IReadOnlyList<float> lineHeights, double y)
    {
        var count = Math.Min(lineTops.Count, lineHeights.Count);
        for (var i = 0; i < count; i++)
        {
            var top = lineTops[i];
            var bottom = top + lineHeights[i];
            if (y >= top && y < bottom)
                return i;
        }

        return -1;
    }

    public static void BuildLineTops(IReadOnlyList<float> lineHeights, IList<float> lineTops)
    {
        float y = 0;
        for (var i = 0; i < lineHeights.Count; i++)
        {
            if (i < lineTops.Count)
                lineTops[i] = y;
            else
                lineTops.Add(y);
            y += lineHeights[i];
        }

        while (lineTops.Count > lineHeights.Count)
            lineTops.RemoveAt(lineTops.Count - 1);
    }
}
