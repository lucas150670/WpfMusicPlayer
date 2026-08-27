// SPDX-License-Identifier: MIT

using WpfMusicPlayer.Helpers;

namespace WpfMusicPlayer.Test;

[TestClass]
public sealed class LyricsLayoutEngineTest
{
    [TestMethod]
    public void MainFontSize_IsOnePointEightTimesSecondaryFontSize()
    {
        Assert.AreEqual(LyricsLayoutEngine.SecondaryFontSize * 1.8f, LyricsLayoutEngine.MainFontSize, 0.0001f);
    }

    [TestMethod]
    public void EaseInOutCubic_StartsAndEndsAtExpectedValues()
    {
        Assert.AreEqual(0f, LyricsLayoutEngine.EaseInOutCubic(0f), 0.0001f);
        Assert.AreEqual(0.5f, LyricsLayoutEngine.EaseInOutCubic(0.5f), 0.0001f);
        Assert.AreEqual(1f, LyricsLayoutEngine.EaseInOutCubic(1f), 0.0001f);
    }

    [TestMethod]
    public void StaggerDelayMilliseconds_OnlyRowsBelowAnchorAreDelayed()
    {
        Assert.AreEqual(0d, LyricsLayoutEngine.StaggerDelayMilliseconds(2, 5), 0.0001);
        Assert.AreEqual(0d, LyricsLayoutEngine.StaggerDelayMilliseconds(5, 5), 0.0001);
        Assert.AreEqual(
            2 * LyricsLayoutEngine.ScrollStaggerPerLineMilliseconds,
            LyricsLayoutEngine.StaggerDelayMilliseconds(7, 5),
            0.0001);
    }

    [TestMethod]
    public void StaggerDelayMilliseconds_CapsDelayAtMaxLines()
    {
        var capped = LyricsLayoutEngine.StaggerDelayMilliseconds(100, 0);
        Assert.AreEqual(
            LyricsLayoutEngine.ScrollStaggerMaxLines * LyricsLayoutEngine.ScrollStaggerPerLineMilliseconds,
            capped,
            0.0001);
    }

    [TestMethod]
    public void MeasureLineHeight_IncludesPaddingAndSecondaryLines()
    {
        var textOnly = LyricsLayoutEngine.MeasureLineHeight(20f, null, null);
        Assert.AreEqual(46f, textOnly, 0.001f);

        var withTranslation = LyricsLayoutEngine.MeasureLineHeight(20f, 14f, null);
        Assert.AreEqual(62f, withTranslation, 0.001f);

        var withBoth = LyricsLayoutEngine.MeasureLineHeight(20f, 14f, 12f);
        Assert.AreEqual(76f, withBoth, 0.001f);
    }

    [TestMethod]
    public void ComputeAnchorOffset_PlacesItemTopAtAnchorRatio()
    {
        var offset = LyricsLayoutEngine.ComputeAnchorOffset(itemTop: 400, viewportHeight: 200, contentHeight: 1000);
        Assert.AreEqual(400d - 200d * LyricsLayoutEngine.ActiveLineAnchorRatio, offset, 0.001);
    }

    [TestMethod]
    public void ComputeAnchorOffset_ClampsToZeroNearContentStart()
    {
        var offset = LyricsLayoutEngine.ComputeAnchorOffset(itemTop: 10, viewportHeight: 400, contentHeight: 1000);
        Assert.AreEqual(0d, offset, 0.001);
    }

    [TestMethod]
    public void ComputeAnchorOffset_ClampsToScrollableRangeNearContentEnd()
    {
        var offset = LyricsLayoutEngine.ComputeAnchorOffset(itemTop: 990, viewportHeight: 200, contentHeight: 1000);
        Assert.AreEqual(800d, offset, 0.001);
    }

    [TestMethod]
    public void HitTestLine_ReturnsMatchingIndex()
    {
        float[] tops = [0f, 40f, 90f];
        float[] heights = [40f, 50f, 30f];

        Assert.AreEqual(0, LyricsLayoutEngine.HitTestLine(tops, heights, 0));
        Assert.AreEqual(1, LyricsLayoutEngine.HitTestLine(tops, heights, 40));
        Assert.AreEqual(1, LyricsLayoutEngine.HitTestLine(tops, heights, 89.9));
        Assert.AreEqual(2, LyricsLayoutEngine.HitTestLine(tops, heights, 90));
        Assert.AreEqual(-1, LyricsLayoutEngine.HitTestLine(tops, heights, 130));
    }

    [TestMethod]
    public void BuildLineTops_AccumulatesHeights()
    {
        float[] heights = [10f, 25f, 15f];
        var tops = new List<float>();
        LyricsLayoutEngine.BuildLineTops(heights, tops);

        Assert.HasCount(3, tops);
        Assert.AreEqual(0f, tops[0], 0.001f);
        Assert.AreEqual(10f, tops[1], 0.001f);
        Assert.AreEqual(35f, tops[2], 0.001f);
        Assert.AreEqual(50d, LyricsLayoutEngine.ComputeContentHeight(heights), 0.001);
    }

    [TestMethod]
    public void LyricSizeMetrics_ComputesLineHeightFromParts()
    {
        var metrics = new LyricsLayoutEngine.LyricSizeMetrics(20f, 14f, 12f);
        Assert.AreEqual(76f, metrics.LineHeight, 0.001f);
    }

    [TestMethod]
    public void ShouldResumeAutoFollow_WaitsForIdleThenLyricChange()
    {
        Assert.IsFalse(LyricsLayoutEngine.ShouldResumeAutoFollow(followSuspended: false, TimeSpan.FromSeconds(20)));
        Assert.IsFalse(LyricsLayoutEngine.ShouldResumeAutoFollow(followSuspended: true, TimeSpan.FromSeconds(9)));
        Assert.IsTrue(LyricsLayoutEngine.ShouldResumeAutoFollow(followSuspended: true, TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public void ClampOffset_RespectsScrollableRange()
    {
        Assert.AreEqual(0d, LyricsLayoutEngine.ClampOffset(-20, 200, 100), 0.001);
        Assert.AreEqual(50d, LyricsLayoutEngine.ClampOffset(80, 200, 250), 0.001);
        Assert.AreEqual(30d, LyricsLayoutEngine.ClampOffset(30, 200, 400), 0.001);
    }
}
