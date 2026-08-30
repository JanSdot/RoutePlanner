namespace TrainingRoutePlanner.OsmCorridors;

/// <summary>Precomputed distance/score prefix-sum profile for one extracted corridor,
/// mirroring corridor_profile()'s (dist, score) return in the Python reference. Both
/// arrays are indexed in lock-step with <see cref="PathNodes"/> and start at 0.0.</summary>
internal sealed class CorridorProfile
{
    public required IReadOnlyList<long> PathNodes { get; init; }
    public required IReadOnlyList<double> Dist { get; init; }
    public required IReadOnlyList<double> Score { get; init; }

    public double TotalLengthMeters => Dist[^1];
}

/// <summary>Ported 1:1 from corridor_profile() in phase0-spike/scripts/corridor_check.py.</summary>
internal static class CorridorProfileBuilder
{
    public static CorridorProfile Build(RoadGraph graph, IReadOnlyList<long> pathNodes)
    {
        int n = pathNodes.Count;
        var dist = new double[n];
        var score = new double[n];

        for (int i = 1; i < n; i++)
        {
            long a = pathNodes[i - 1];
            long b = pathNodes[i];
            double edgeLen = graph.GetEdge(a, b).LengthMeters;
            dist[i] = dist[i - 1] + edgeLen;

            double? tagScore = CorridorScoring.StaticTagScore(graph, b);
            double nodeScore;
            if (tagScore == HighwayTags.HardExclusion)
            {
                nodeScore = 0.0; // endpoint is the corridor boundary, not a traversed crossing
            }
            else if (tagScore.HasValue)
            {
                nodeScore = tagScore.Value;
            }
            else if (i + 1 < n)
            {
                nodeScore = CorridorScoring.JunctionPenalty(graph, a, b, pathNodes[i + 1]);
            }
            else
            {
                nodeScore = 0.0; // corridor end (dead end), no further crossing to evaluate
            }

            score[i] = score[i - 1] + nodeScore;
        }

        return new CorridorProfile { PathNodes = pathNodes, Dist = dist, Score = score };
    }
}
