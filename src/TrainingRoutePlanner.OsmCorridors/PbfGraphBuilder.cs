using OsmSharp;
using OsmSharp.Streams;
using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.OsmCorridors;

/// <summary>Builds a <see cref="RoadGraph"/> from a .osm.pbf file using OsmSharp.
///
/// OsmSharp's raw Way objects only carry node ID references (long[]), not resolved
/// coordinates. Two strategies were considered:
///   (a) OsmSharp's "complete" stream helpers (ToComplete()/OsmSimpleCompleteStreamSource),
///       which resolve CompleteWay.Nodes for you but do so by caching ALL nodes/ways from
///       the file in memory internally - for a 189MB regional extract that's a lot of
///       coordinate data we don't need (most nodes aren't part of a relevant road way).
///   (b) A manual two-pass approach.
/// We use (b): pass 1 streams the whole file once, collecting hard/give-way node tags
/// (cheap - a tiny fraction of nodes carry these) AND the id/tag/node-ref data of only the
/// ways whose highway tag is in HighwayTags.RoadHighwayTypes (also a fraction of all ways).
/// This yields the exact set of node IDs we actually need coordinates for. Pass 2 streams
/// the file a second time, resolving Lat/Lon only for that needed-node-ID set. This keeps
/// peak memory proportional to "nodes touched by relevant roads" rather than "every node in
/// the extract", at the cost of reading the file twice - an explicit, documented tradeoff
/// rather than relying on any assumption about node/way ordering within the pbf.</summary>
internal static class PbfGraphBuilder
{
    private readonly record struct RelevantWay(string Highway, string? Junction, long[] NodeIds);

    public static RoadGraph Build(string pbfPath)
    {
        var graph = new RoadGraph();
        var relevantWays = new List<RelevantWay>();
        var neededNodeIds = new HashSet<long>();

        using (var stream = File.OpenRead(pbfPath))
        {
            var source = new PBFOsmStreamSource(stream);
            foreach (var osmGeo in source)
            {
                switch (osmGeo)
                {
                    case Node node:
                        CollectNodeTags(graph, node);
                        break;
                    case Way way:
                        CollectRelevantWay(way, relevantWays, neededNodeIds);
                        break;
                }
            }
        }

        using (var stream = File.OpenRead(pbfPath))
        {
            var source = new PBFOsmStreamSource(stream);
            foreach (var osmGeo in source)
            {
                if (osmGeo is Node node
                    && node.Id.HasValue
                    && neededNodeIds.Contains(node.Id.Value)
                    && node.Latitude.HasValue
                    && node.Longitude.HasValue)
                {
                    graph.SetCoordinate(node.Id.Value, new GeoPoint(node.Latitude.Value, node.Longitude.Value));
                }
            }
        }

        foreach (var way in relevantWays)
        {
            AddWayEdges(graph, way);
        }

        return graph;
    }

    private static void CollectNodeTags(RoadGraph graph, Node node)
    {
        if (!node.Id.HasValue || node.Tags is null)
        {
            return;
        }

        if (!node.Tags.TryGetValue("highway", out var highway))
        {
            return;
        }

        if (highway is "traffic_signals" or "stop")
        {
            graph.HardNodes.Add(node.Id.Value);
            graph.HardNodeTypes[node.Id.Value] = highway == "traffic_signals" ? HardNodeType.TrafficSignal : HardNodeType.Stop;
        }
        else if (highway == "give_way")
        {
            graph.GiveWayNodes.Add(node.Id.Value);
        }
    }

    private static void CollectRelevantWay(Way way, List<RelevantWay> relevantWays, HashSet<long> neededNodeIds)
    {
        if (way.Tags is null || !way.Tags.TryGetValue("highway", out var highway))
        {
            return;
        }

        if (!HighwayTags.RoadHighwayTypes.Contains(highway))
        {
            return;
        }

        way.Tags.TryGetValue("junction", out var junction);
        var nodeIds = way.Nodes ?? [];
        relevantWays.Add(new RelevantWay(highway, junction, nodeIds));
        foreach (var id in nodeIds)
        {
            neededNodeIds.Add(id);
        }
    }

    private static void AddWayEdges(RoadGraph graph, RelevantWay way)
    {
        long? prevId = null;
        foreach (var nodeId in way.NodeIds)
        {
            if (!graph.Coordinates.ContainsKey(nodeId))
            {
                // Mirrors the Python handler's `if not nref.location.valid(): prev = None; continue` -
                // an unresolved coordinate breaks the edge chain at that point rather than
                // silently connecting across the gap.
                prevId = null;
                continue;
            }

            if (prevId.HasValue)
            {
                if (!graph.HasEdge(prevId.Value, nodeId))
                {
                    var a = graph.Coordinates[prevId.Value];
                    var b = graph.Coordinates[nodeId];
                    double length = GeoMath.HaversineMeters(a, b);
                    graph.AddEdge(prevId.Value, nodeId, length, way.Highway);
                }

                if (way.Junction == "roundabout")
                {
                    graph.RoundaboutNodes.Add(prevId.Value);
                    graph.RoundaboutNodes.Add(nodeId);
                }
            }

            prevId = nodeId;
        }
    }
}
