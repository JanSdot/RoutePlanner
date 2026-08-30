using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.OsmCorridors;

namespace TrainingRoutePlanner.Tests;

public class CorridorScoringTests
{
    [Fact]
    public void StaticTagScore_HardExclusion_ForTrafficSignalsOrStop()
    {
        var graph = new RoadGraph();
        graph.HardNodes.Add(1);

        Assert.Equal(double.PositiveInfinity, CorridorScoring.StaticTagScore(graph, 1));
    }

    [Fact]
    public void StaticTagScore_IsNull_ForUnmarkedNode()
    {
        var graph = new RoadGraph();

        Assert.Null(CorridorScoring.StaticTagScore(graph, 42));
    }

    /// <summary>Regression test for CONCEPT.md 6.1 bug #3: an unmarked crossing must NOT get
    /// a single flat penalty regardless of road class. When our direction of travel is
    /// strictly higher-ranked than every crossing road, it's de-facto priority (low penalty).
    /// Otherwise - including the same-class case, which is the common "Rechts vor links"
    /// situation under German traffic law - it must score the SAME as an explicit give-way
    /// sign (meaningfully higher than de-facto priority).</summary>
    [Fact]
    public void JunctionPenalty_SameClassCrossing_ScoresHigherThan_LowerClassCrossing()
    {
        // Scenario A: "Rechts vor links" - our road and the crossing road are both residential.
        var rechtsVorLinksGraph = new RoadGraph();
        rechtsVorLinksGraph.AddEdge(1, 2, 100.0, "residential"); // prev -> cur
        rechtsVorLinksGraph.AddEdge(2, 3, 100.0, "residential"); // cur -> next
        rechtsVorLinksGraph.AddEdge(2, 4, 100.0, "residential"); // crossing branch, same class

        double rechtsVorLinksScore = CorridorScoring.JunctionPenalty(rechtsVorLinksGraph, prev: 1, cur: 2, next: 3);

        // Scenario B: de-facto priority - our road is secondary, the crossing branch is only residential.
        var defactoPriorityGraph = new RoadGraph();
        defactoPriorityGraph.AddEdge(1, 2, 100.0, "secondary");
        defactoPriorityGraph.AddEdge(2, 3, 100.0, "secondary");
        defactoPriorityGraph.AddEdge(2, 4, 100.0, "residential"); // crossing branch, lower class

        double defactoPriorityScore = CorridorScoring.JunctionPenalty(defactoPriorityGraph, prev: 1, cur: 2, next: 3);

        Assert.Equal(HighwayTagsTestAccess.RechtsVorLinksPenalty, rechtsVorLinksScore);
        Assert.Equal(HighwayTagsTestAccess.DefactoPriorityPenalty, defactoPriorityScore);
        Assert.True(
            rechtsVorLinksScore > defactoPriorityScore,
            "Rechts-vor-links (unmarked, same-class crossing) must score meaningfully higher " +
            "than de-facto priority (unmarked, clearly-lower-class crossing).");
    }

    [Fact]
    public void JunctionPenalty_IsZero_WhenNodeDegreeBelowThree()
    {
        var graph = new RoadGraph();
        graph.AddEdge(1, 2, 100.0, "residential");
        graph.AddEdge(2, 3, 100.0, "residential");
        // node 2 has degree 2 - no crossing at all, just passing through.

        double score = CorridorScoring.JunctionPenalty(graph, prev: 1, cur: 2, next: 3);

        Assert.Equal(0.0, score);
    }
}

/// <summary>Small helper exposing the internal score constants under a stable test-only name,
/// so the tests above read as assertions against the documented CONCEPT.md 3.4 values rather
/// than duplicating magic numbers.</summary>
internal static class HighwayTagsTestAccess
{
    public const double RechtsVorLinksPenalty = HighwayTags.RechtsVorLinksPenalty;
    public const double DefactoPriorityPenalty = HighwayTags.DefactoPriorityPenalty;
}
