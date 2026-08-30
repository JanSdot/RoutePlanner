using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.OsmCorridors;

internal readonly record struct RoadEdge(double LengthMeters, string Highway);

/// <summary>Plain in-memory undirected road graph - the shared data structure that both the
/// OsmSharp-based pbf loader and hand-built test fixtures produce, so the corridor algorithm
/// itself never depends on OsmSharp or pbf files (see CONCEPT.md testing requirements).
/// Mirrors the shape of the Python reference's networkx.Graph (nodes = OSM node ids,
/// edges keyed by both endpoints, no duplicate edge on repeated node pairs).</summary>
internal sealed class RoadGraph
{
    private readonly Dictionary<long, GeoPoint> _coordinates = new();
    private readonly Dictionary<long, Dictionary<long, RoadEdge>> _adjacency = new();

    public HashSet<long> HardNodes { get; } = new();
    public HashSet<long> GiveWayNodes { get; } = new();
    public HashSet<long> RoundaboutNodes { get; } = new();

    public IReadOnlyDictionary<long, GeoPoint> Coordinates => _coordinates;

    public IEnumerable<long> Nodes => _adjacency.Keys;

    public void SetCoordinate(long nodeId, GeoPoint point) => _coordinates[nodeId] = point;

    public bool HasEdge(long a, long b) => _adjacency.TryGetValue(a, out var nbrs) && nbrs.ContainsKey(b);

    public void AddEdge(long a, long b, double lengthMeters, string highway)
    {
        if (HasEdge(a, b))
        {
            return;
        }

        var edge = new RoadEdge(lengthMeters, highway);
        AddDirected(a, b, edge);
        AddDirected(b, a, edge);
    }

    private void AddDirected(long from, long to, RoadEdge edge)
    {
        if (!_adjacency.TryGetValue(from, out var nbrs))
        {
            nbrs = new Dictionary<long, RoadEdge>();
            _adjacency[from] = nbrs;
        }

        nbrs[to] = edge;
    }

    public IEnumerable<long> Neighbors(long node) =>
        _adjacency.TryGetValue(node, out var nbrs) ? nbrs.Keys : Enumerable.Empty<long>();

    public int Degree(long node) => _adjacency.TryGetValue(node, out var nbrs) ? nbrs.Count : 0;

    public RoadEdge GetEdge(long a, long b) => _adjacency[a][b];
}
