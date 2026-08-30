namespace TrainingRoutePlanner.OsmCorridors;

/// <summary>Ported 1:1 from static_tag_score / junction_penalty in
/// phase0-spike/scripts/corridor_check.py.</summary>
internal static class CorridorScoring
{
    /// <summary>Score contribution that does NOT depend on direction of travel (fixed OSM
    /// tags: signal/stop/roundabout/give-way). Null means "unmarked" - direction- and
    /// road-class-dependent, see <see cref="JunctionPenalty"/>.</summary>
    public static double? StaticTagScore(RoadGraph graph, long node)
    {
        if (graph.HardNodes.Contains(node))
        {
            return HighwayTags.HardExclusion;
        }

        if (graph.RoundaboutNodes.Contains(node))
        {
            return HighwayTags.RoundaboutPenalty;
        }

        if (graph.GiveWayNodes.Contains(node))
        {
            return HighwayTags.GiveWayPenalty;
        }

        return null;
    }

    /// <summary>Score contribution of an unmarked crossing: compares our direction of travel's
    /// road class against the crossing roads. Only when we are strictly higher-ranked do we
    /// have de-facto priority; otherwise German traffic law's "Rechts vor links" applies,
    /// which forces the same look/brake behavior as a give-way sign. This distinction is
    /// bug #3 from CONCEPT.md 6.1 - do NOT collapse this back into a single flat penalty for
    /// all unmarked crossings.</summary>
    public static double JunctionPenalty(RoadGraph graph, long prev, long cur, long next)
    {
        if (graph.Degree(cur) < 3)
        {
            return 0.0;
        }

        int incomingRank = HighwayTags.HighwayRank.GetValueOrDefault(graph.GetEdge(prev, cur).Highway, 0);
        var crossingRanks = graph.Neighbors(cur)
            .Where(n => n != prev && n != next)
            .Select(n => HighwayTags.HighwayRank.GetValueOrDefault(graph.GetEdge(cur, n).Highway, 0))
            .ToList();

        if (crossingRanks.Count == 0)
        {
            return 0.0;
        }

        return incomingRank > crossingRanks.Max()
            ? HighwayTags.DefactoPriorityPenalty
            : HighwayTags.RechtsVorLinksPenalty;
    }
}
