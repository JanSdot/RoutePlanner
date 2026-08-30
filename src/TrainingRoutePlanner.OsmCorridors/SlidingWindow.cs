namespace TrainingRoutePlanner.OsmCorridors;

internal static class SlidingWindow
{
    internal readonly record struct WindowResult(double Score, int Left, int Right);

    /// <summary>Ported 1:1 from best_window() in phase0-spike/scripts/corridor_check.py.
    /// `left` only advances when the window that would RESULT from advancing it is still
    /// valid (still &gt;= minLenMeters) - checked via lookahead (dist[right] - dist[left+1])
    /// BEFORE incrementing, not by checking the window size after the fact. This is bug #2
    /// from CONCEPT.md 6.1: with coarse edge lengths (long rural roads with few shape
    /// points), advancing left based on the current/prior window size can jump straight
    /// past the minimum-length threshold and skip a valid window entirely. Do not "simplify"
    /// this loop - the lookahead-before-advancing order is the fix.</summary>
    public static WindowResult? BestWindow(IReadOnlyList<double> dist, IReadOnlyList<double> score, double minLenMeters)
    {
        int n = dist.Count;
        WindowResult? best = null;
        int left = 0;

        for (int right = 0; right < n; right++)
        {
            while (left + 1 <= right && dist[right] - dist[left + 1] >= minLenMeters)
            {
                left++;
            }

            if (dist[right] - dist[left] >= minLenMeters)
            {
                double windowScore = score[right] - score[left];
                if (best is null || windowScore < best.Value.Score)
                {
                    best = new WindowResult(windowScore, left, right);
                }
            }
        }

        return best;
    }
}
