namespace TrainingRoutePlanner.OsmCorridors;

/// <summary>Ported 1:1 from extract_corridors / pick_straightest in
/// phase0-spike/scripts/corridor_check.py. Walks only START from hard-exclusion nodes
/// (traffic signals/stop), but CONTINUES through any node that is not itself a hard
/// exclusion - including real degree&gt;=3 junctions - picking the straightest
/// continuation at branches. This is bug #1 from CONCEPT.md 6.1: an earlier version
/// stopped at every junction instead of only hard-exclusion ones, which is wrong per
/// CONCEPT.md 4.1 ("laeuft durch alle weichen Kreuzungen hindurch").</summary>
internal static class CorridorExtractor
{
    public static List<List<long>> ExtractCorridors(RoadGraph graph)
    {
        var visitedEdges = new HashSet<(long, long)>();
        var corridors = new List<List<long>>();

        bool IsHard(long node) => CorridorScoring.StaticTagScore(graph, node) == HighwayTags.HardExclusion;

        foreach (var startNode in graph.Nodes.ToList())
        {
            if (!IsHard(startNode))
            {
                continue; // only start from hard-exclusion nodes
            }

            foreach (var neighbor in graph.Neighbors(startNode).ToList())
            {
                var edgeKey = EdgeKey(startNode, neighbor);
                if (visitedEdges.Contains(edgeKey))
                {
                    continue;
                }

                var pathNodes = new List<long> { startNode, neighbor };
                visitedEdges.Add(edgeKey);
                long prev = startNode;
                long cur = neighbor;

                while (!IsHard(cur))
                {
                    var candidates = graph.Neighbors(cur)
                        .Where(n => n != prev && !visitedEdges.Contains(EdgeKey(cur, n)))
                        .ToList();
                    if (candidates.Count == 0)
                    {
                        break; // dead end
                    }

                    long next = PickStraightest(graph, prev, cur, candidates);
                    visitedEdges.Add(EdgeKey(cur, next));
                    pathNodes.Add(next);
                    prev = cur;
                    cur = next;
                }

                if (pathNodes.Count >= 2)
                {
                    corridors.Add(pathNodes);
                }
            }
        }

        return corridors;
    }

    /// <summary>At a soft crossing, pick the direction that most nearly continues straight
    /// on - approximates how a cyclist would follow the road rather than turning at random.</summary>
    internal static long PickStraightest(RoadGraph graph, long prevId, long curId, IReadOnlyList<long> candidates)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var prevPoint = graph.Coordinates[prevId];
        var curPoint = graph.Coordinates[curId];
        double incoming = GeoMath.BearingDegrees(prevPoint, curPoint);

        long best = candidates[0];
        double bestDiff = double.MaxValue;
        foreach (var candidate in candidates)
        {
            var candidatePoint = graph.Coordinates[candidate];
            double outgoing = GeoMath.BearingDegrees(curPoint, candidatePoint);
            double diff = GeoMath.AngleDiff(incoming, outgoing);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = candidate;
            }
        }

        return best;
    }

    private static (long, long) EdgeKey(long a, long b) => a < b ? (a, b) : (b, a);
}
