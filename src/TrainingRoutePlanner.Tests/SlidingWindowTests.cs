using TrainingRoutePlanner.OsmCorridors;

namespace TrainingRoutePlanner.Tests;

public class SlidingWindowTests
{
    /// <summary>Regression test for CONCEPT.md 6.1 bug #2. Mirrors a corridor with coarse
    /// edge lengths (a long rural road with few shape points): a 5000m edge, a tiny 1m edge,
    /// a 5000m edge, another tiny 1m edge, with a costly crossing right after the first big
    /// edge and another right at the very end.
    ///
    /// dist = [0, 5000, 5001, 10001, 10002]
    /// score = [0, 100, 100, 100, 200]
    ///
    /// The only zero-score windows of length &gt;= 5000 are [1,3] and [2,3] (crossing the
    /// low-cost stretch between the two costly nodes). A naive two-pointer that advances
    /// `left` while the CURRENT window (before checking the resulting one) is still >= minLen
    /// overshoots: at right=3 it would walk left from 0 all the way to 3 in one go (each
    /// intermediate window still being >= minLen against the old dist[right] triggers another
    /// advance), stepping straight past both good windows and ending with left == right (an
    /// invalid zero-length window) - i.e. it reports NO valid window at all for right=1 and
    /// right=3, and misses the score-0 answer entirely. The lookahead-before-advancing version
    /// ported from the Python reference must find it.</summary>
    [Fact]
    public void BestWindow_CoarseEdges_FindsValidWindow_ThatNaiveAdvanceWouldSkip()
    {
        double[] dist = [0, 5000, 5001, 10001, 10002];
        double[] score = [0, 100, 100, 100, 200];
        const double minLen = 5000;

        var result = SlidingWindow.BestWindow(dist, score, minLen);

        Assert.NotNull(result);
        Assert.Equal(0.0, result!.Value.Score);
        Assert.True(dist[result.Value.Right] - dist[result.Value.Left] >= minLen);
        Assert.Equal(2, result.Value.Left);
        Assert.Equal(3, result.Value.Right);
    }

    [Fact]
    public void BestWindow_ReturnsNull_WhenCorridorShorterThanMinLength()
    {
        double[] dist = [0, 500, 900];
        double[] score = [0, 0, 0];

        var result = SlidingWindow.BestWindow(dist, score, minLenMeters: 1000);

        Assert.Null(result);
    }

    [Fact]
    public void BestWindow_PicksLowestScoreWindow_AmongSeveralValidOnes()
    {
        // Two disjoint valid windows of exactly minLen: [0,1] with score 5, [2,3] with score 1.
        double[] dist = [0, 1000, 1000, 2000];
        double[] score = [0, 5, 5, 6];

        var result = SlidingWindow.BestWindow(dist, score, minLenMeters: 1000);

        Assert.NotNull(result);
        Assert.Equal(1.0, result!.Value.Score);
        Assert.Equal(2, result.Value.Left);
        Assert.Equal(3, result.Value.Right);
    }
}
