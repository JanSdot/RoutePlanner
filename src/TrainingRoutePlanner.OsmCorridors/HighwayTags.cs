namespace TrainingRoutePlanner.OsmCorridors;

/// <summary>OSM tag constants and score weights, ported 1:1 from
/// phase0-spike/scripts/corridor_check.py (ROAD_HIGHWAY_TYPES, HIGHWAY_RANK, and the
/// score-weight constants). See CONCEPT.md section 3.4 for the rationale; these are
/// explicitly documented as placeholder values to be recalibrated in Phase 3.</summary>
internal static class HighwayTags
{
    /// <summary>Relevant for road-bike training - foot/cycle/service ways deliberately
    /// excluded (surface/relevance), see CONCEPT.md 3.4.</summary>
    internal static readonly HashSet<string> RoadHighwayTypes = new()
    {
        "trunk", "trunk_link",
        "primary", "primary_link",
        "secondary", "secondary_link",
        "tertiary", "tertiary_link",
        "unclassified", "residential", "living_street",
    };

    internal const double HardExclusion = double.PositiveInfinity;
    internal const double RoundaboutPenalty = 2.0;
    internal const double GiveWayPenalty = 1.0;
    internal const double DefactoPriorityPenalty = 0.2;
    internal const double RechtsVorLinksPenalty = 1.0;

    /// <summary>Rough German road-class ranking used as a right-of-way proxy at unmarked
    /// crossings (OSM rarely tags actual priority regulation directly).</summary>
    internal static readonly Dictionary<string, int> HighwayRank = new()
    {
        ["trunk"] = 6,
        ["trunk_link"] = 6,
        ["primary"] = 5,
        ["primary_link"] = 5,
        ["secondary"] = 4,
        ["secondary_link"] = 4,
        ["tertiary"] = 3,
        ["tertiary_link"] = 3,
        ["unclassified"] = 2,
        ["residential"] = 1,
        ["living_street"] = 0,
    };
}
