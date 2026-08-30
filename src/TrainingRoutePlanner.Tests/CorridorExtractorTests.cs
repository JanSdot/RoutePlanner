using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.OsmCorridors;

namespace TrainingRoutePlanner.Tests;

public class CorridorExtractorTests
{
    /// <summary>Regression test for CONCEPT.md 6.1 bug #1: corridor extraction must continue
    /// through a real degree&gt;=3 junction (node 3 below has a branch to node 6) as long as
    /// it is not itself a hard-exclusion node, and only stop at the next hard-exclusion node
    /// (node 5). A broken implementation that stops at every junction would produce a
    /// 3-node corridor [1,2,3] instead of the full [1,2,3,4,5] chain.</summary>
    [Fact]
    public void ExtractCorridors_ContinuesThroughSoftJunction_StopsOnlyAtHardExclusionNode()
    {
        var graph = new RoadGraph();
        // Roughly straight west-to-east chain; node 6 branches off north at node 3.
        graph.SetCoordinate(1, new GeoPoint(52.500, 13.400));
        graph.SetCoordinate(2, new GeoPoint(52.500, 13.401));
        graph.SetCoordinate(3, new GeoPoint(52.500, 13.402));
        graph.SetCoordinate(4, new GeoPoint(52.500, 13.403));
        graph.SetCoordinate(5, new GeoPoint(52.500, 13.404));
        graph.SetCoordinate(6, new GeoPoint(52.510, 13.402));

        graph.HardNodes.Add(1); // traffic_signals / stop
        graph.HardNodes.Add(5);

        graph.AddEdge(1, 2, 100.0, "residential");
        graph.AddEdge(2, 3, 100.0, "residential");
        graph.AddEdge(3, 4, 100.0, "residential"); // straight-ahead continuation at the junction
        graph.AddEdge(4, 5, 100.0, "residential");
        graph.AddEdge(3, 6, 100.0, "residential"); // branch - node 3 has degree 3

        var corridors = CorridorExtractor.ExtractCorridors(graph);

        var mainCorridor = Assert.Single(corridors);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, mainCorridor);
    }

    [Fact]
    public void ExtractCorridors_DoesNotStartFromNonHardNodes()
    {
        var graph = new RoadGraph();
        graph.SetCoordinate(1, new GeoPoint(52.500, 13.400));
        graph.SetCoordinate(2, new GeoPoint(52.500, 13.401));
        // No hard nodes at all - a dead-end-only graph should still yield exactly one
        // corridor per physical chain, discovered from... nowhere, since nothing is hard.
        graph.AddEdge(1, 2, 100.0, "residential");

        var corridors = CorridorExtractor.ExtractCorridors(graph);

        Assert.Empty(corridors);
    }

    [Fact]
    public void ExtractCorridors_StopsAtDeadEnd_WhenNoHardNodeReached()
    {
        var graph = new RoadGraph();
        graph.SetCoordinate(1, new GeoPoint(52.500, 13.400));
        graph.SetCoordinate(2, new GeoPoint(52.500, 13.401));
        graph.SetCoordinate(3, new GeoPoint(52.500, 13.402));

        graph.HardNodes.Add(1);
        graph.AddEdge(1, 2, 100.0, "residential");
        graph.AddEdge(2, 3, 100.0, "residential"); // node 3 is a dead end (degree 1), never hard

        var corridors = CorridorExtractor.ExtractCorridors(graph);

        var corridor = Assert.Single(corridors);
        Assert.Equal(new long[] { 1, 2, 3 }, corridor);
    }
}
