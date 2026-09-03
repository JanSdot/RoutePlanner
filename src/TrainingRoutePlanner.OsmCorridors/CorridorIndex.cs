using System.Linq;
using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.OsmCorridors;

/// <summary>Public entry point for the corridor pre-computation and query pipeline
/// (CONCEPT.md section 4.1). Build once per region with <see cref="Load"/> (or, for tests,
/// via the internal graph-based constructor against a hand-built <see cref="RoadGraph"/> -
/// see CONCEPT.md testing requirements), then answer many cheap
/// <see cref="TryFindCorridor"/> queries against it.</summary>
public sealed class CorridorIndex : ICorridorIndex
{
    private readonly RoadGraph _graph;
    private readonly List<CorridorProfile> _corridors;
    private readonly List<BoundingBox> _bboxes;
    private readonly Dictionary<(int, int), List<long>> _hardNodeGrid;
    private readonly Dictionary<long, long> _hardNodeClusterRoot;

    private readonly record struct BoundingBox(double MinLat, double MaxLat, double MinLon, double MaxLon);

    // Grobes Gitter ueber die Ampel-/Stopp-Knoten (RoadGraph.HardNodes) fuer
    // CountDisruptiveJunctionsNear - kein generischer Spatial Index (siehe TryFindCorridor-
    // Kommentar zum linearen Scan ueber Korridore, das bleibt ein bekannter Folgeschritt),
    // sondern ein einfaches Bucket-Gitter extra fuer diese eine Abfrageart. Zellgroesse deutlich
    // groesser als jede sinnvolle proximityMeters-Anfrage, damit ein 3x3-Nachbarzellen-Scan
    // garantiert alle Kandidaten findet, auch nahe an Zellgrenzen.
    private const double HardNodeGridCellMeters = 200.0;

    // OSM modelliert eine einzelne reale Ampel-/Stopp-Kreuzung oft mit MEHREREN Knoten (ein
    // Knoten je Anfahrt/Fahrspur, besonders bei groesseren Berliner Kreuzungen), die laut
    // Recherche typischerweise wenige Meter bis ~20m auseinanderliegen. Live gemeldeter Bug:
    // eine reale Route zeigte "57 Ampel-/Stopp-Kreuzungen" an, weil CountDisruptiveJunctionsNear
    // urspruenglich nach roher OSM-Node-ID statt nach physischer Kreuzung deduplizierte - eine
    // grosse Kreuzung mit 3-4 Signal-Knoten zaehlte entsprechend 3-4 mal. 20m als Cluster-Radius
    // ist ein bewusster Kompromiss: gross genug, um die ueblichen Mehrfach-Knoten EINER Kreuzung
    // zusammenzufassen, aber (ausser in sehr dicht bebauten Innenstadtbloecken) klein genug, um
    // zwei tatsaechlich unterschiedliche, nahe beieinanderliegende Kreuzungen nicht faelschlich
    // zu verschmelzen.
    private const double JunctionClusterRadiusMeters = 20.0;

    internal CorridorIndex(RoadGraph graph)
    {
        _graph = graph;
        var rawCorridors = CorridorExtractor.ExtractCorridors(graph);
        _corridors = new List<CorridorProfile>(rawCorridors.Count);
        _bboxes = new List<BoundingBox>(rawCorridors.Count);

        foreach (var pathNodes in rawCorridors)
        {
            _corridors.Add(CorridorProfileBuilder.Build(graph, pathNodes));
            _bboxes.Add(ComputeBoundingBox(graph, pathNodes));
        }

        _hardNodeGrid = BuildHardNodeGrid(graph);
        _hardNodeClusterRoot = BuildHardNodeClusters(graph, _hardNodeGrid);
    }

    /// <summary>Siehe <see cref="ICorridorIndex.CountDisruptiveJunctionsNear"/>.</summary>
    public int CountDisruptiveJunctionsNear(IReadOnlyList<GeoPoint> routeGeometry, double proximityMeters)
    {
        var found = new HashSet<long>();
        foreach (var point in routeGeometry)
        {
            var (cellX, cellY) = HardNodeGridCell(point);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!_hardNodeGrid.TryGetValue((cellX + dx, cellY + dy), out var candidates))
                        continue;

                    foreach (var nodeId in candidates)
                    {
                        if (found.Contains(nodeId))
                            continue;
                        if (GeoMath.HaversineMeters(point, _graph.Coordinates[nodeId]) <= proximityMeters)
                            found.Add(nodeId);
                    }
                }
            }
        }

        // Auf physische Kreuzungen statt roher OSM-Node-ID deduplizieren - siehe
        // JunctionClusterRadiusMeters.
        var clusters = new HashSet<long>();
        foreach (var nodeId in found)
            clusters.Add(_hardNodeClusterRoot[nodeId]);
        return clusters.Count;
    }

    /// <summary>Siehe <see cref="ICorridorIndex.GetAllJunctions"/>.</summary>
    public IReadOnlyList<Junction> GetAllJunctions()
    {
        var result = new List<Junction>(_graph.HardNodeTypes.Count);
        foreach (var (nodeId, type) in _graph.HardNodeTypes)
        {
            if (_graph.Coordinates.TryGetValue(nodeId, out var point))
                result.Add(new Junction(point, type));
        }
        return result;
    }

    private static Dictionary<(int, int), List<long>> BuildHardNodeGrid(RoadGraph graph)
    {
        var grid = new Dictionary<(int, int), List<long>>();
        foreach (var nodeId in graph.HardNodes)
        {
            if (!graph.Coordinates.TryGetValue(nodeId, out var point))
                continue;

            var cell = HardNodeGridCell(point);
            if (!grid.TryGetValue(cell, out var list))
            {
                list = new List<long>();
                grid[cell] = list;
            }
            list.Add(nodeId);
        }
        return grid;
    }

    // Union-Find ueber alle Ampel-/Stopp-Knoten: zwei Knoten landen im selben Cluster, sobald
    // eine Kette von Paaren mit je hoechstens JunctionClusterRadiusMeters Abstand sie verbindet
    // (nicht nur direkte Paare - bei einer Kreuzung mit 3+ Signal-Knoten in einer Reihe reicht
    // das, um trotzdem alle in EINEN Cluster zu bekommen). Nutzt dasselbe Bucket-Gitter wie
    // CountDisruptiveJunctionsNear fuer die Kandidatensuche - die Gitterzelle (200m) ist deutlich
    // groesser als der Cluster-Radius (20m), ein 3x3-Nachbarzellen-Scan findet also garantiert
    // alle Kandidaten.
    private static Dictionary<long, long> BuildHardNodeClusters(RoadGraph graph, Dictionary<(int, int), List<long>> grid)
    {
        var parent = new Dictionary<long, long>();
        foreach (var nodeId in graph.HardNodes)
            parent[nodeId] = nodeId;

        long Find(long x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(long a, long b)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (rootA != rootB)
                parent[rootA] = rootB;
        }

        foreach (var nodeId in graph.HardNodes)
        {
            if (!graph.Coordinates.TryGetValue(nodeId, out var point))
                continue;

            var (cellX, cellY) = HardNodeGridCell(point);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!grid.TryGetValue((cellX + dx, cellY + dy), out var candidates))
                        continue;

                    foreach (var otherNodeId in candidates)
                    {
                        if (otherNodeId <= nodeId)
                            continue; // jedes Paar nur einmal betrachten
                        if (GeoMath.HaversineMeters(point, graph.Coordinates[otherNodeId]) <= JunctionClusterRadiusMeters)
                            Union(nodeId, otherNodeId);
                    }
                }
            }
        }

        return graph.HardNodes.ToDictionary(nodeId => nodeId, Find);
    }

    private static (int, int) HardNodeGridCell(GeoPoint p) => (
        (int)Math.Floor(p.Lat * 111_320.0 / HardNodeGridCellMeters),
        (int)Math.Floor(p.Lon * 111_320.0 * Math.Cos(GeoMath.DegreesToRadians(p.Lat)) / HardNodeGridCellMeters));

    /// <summary>Builds the full graph from a .osm.pbf file and extracts + scores all
    /// corridors once. Expensive (pbf parse + graph walk) - meant to run once per region
    /// and be cached/reused, not per request (see CONCEPT.md 4.1).</summary>
    public static CorridorIndex Load(string pbfPath)
    {
        var graph = PbfGraphBuilder.Build(pbfPath);
        return new CorridorIndex(graph);
    }

    /// <summary>Number of extracted corridors (chains between hard-exclusion nodes / dead
    /// ends). Exposed mainly for diagnostics/sanity-checking against CONCEPT.md 6.1's
    /// reference counts.</summary>
    public int CorridorCount => _corridors.Count;

    /// <summary>Finds a corridor sub-window of at least <paramref name="minLengthMeters"/>
    /// whose disruption score is at most <paramref name="maxDisruptionScore"/> and whose
    /// geometry passes within <paramref name="searchRadiusMeters"/> of
    /// <paramref name="near"/>.
    ///
    /// Scaling note: this does a full linear scan over all precomputed corridors (with a
    /// cheap lat/lon bounding-box pre-filter to skip the expensive per-segment distance
    /// check for corridors nowhere near the point). Acceptable for the Phase-1 MVP per
    /// CONCEPT.md; a real spatial index (R-tree/grid) is a known follow-up once corridor
    /// counts and query volume actually make the linear scan a bottleneck.</summary>
    public Corridor? TryFindCorridor(
        GeoPoint near,
        double minLengthMeters,
        double maxDisruptionScore,
        double searchRadiusMeters)
    {
        double latMarginDeg = searchRadiusMeters / 111_320.0;
        double lonMarginDeg = searchRadiusMeters / (111_320.0 * Math.Cos(GeoMath.DegreesToRadians(near.Lat)));

        Corridor? best = null;
        double bestDistanceMeters = double.MaxValue;

        for (int i = 0; i < _corridors.Count; i++)
        {
            var profile = _corridors[i];
            if (profile.TotalLengthMeters < minLengthMeters)
            {
                continue;
            }

            var bbox = _bboxes[i];
            if (near.Lat < bbox.MinLat - latMarginDeg || near.Lat > bbox.MaxLat + latMarginDeg
                || near.Lon < bbox.MinLon - lonMarginDeg || near.Lon > bbox.MaxLon + lonMarginDeg)
            {
                continue;
            }

            var window = SlidingWindow.BestWindow(profile.Dist, profile.Score, minLengthMeters);
            if (window is null || window.Value.Score > maxDisruptionScore)
            {
                continue;
            }

            var geometry = BuildGeometry(profile, window.Value.Left, window.Value.Right);
            double distanceMeters = MinDistanceToPolyline(near, geometry);
            if (distanceMeters > searchRadiusMeters)
            {
                continue;
            }

            if (distanceMeters < bestDistanceMeters)
            {
                bestDistanceMeters = distanceMeters;
                best = new Corridor
                {
                    Start = geometry[0],
                    End = geometry[^1],
                    LengthMeters = profile.Dist[window.Value.Right] - profile.Dist[window.Value.Left],
                    DisruptionScore = window.Value.Score,
                    Geometry = geometry,
                };
            }
        }

        return best;
    }

    private List<GeoPoint> BuildGeometry(CorridorProfile profile, int left, int right)
    {
        var geometry = new List<GeoPoint>(right - left + 1);
        for (int i = left; i <= right; i++)
        {
            geometry.Add(_graph.Coordinates[profile.PathNodes[i]]);
        }

        return geometry;
    }

    private static double MinDistanceToPolyline(GeoPoint p, IReadOnlyList<GeoPoint> geometry)
    {
        if (geometry.Count == 1)
        {
            return GeoMath.HaversineMeters(p, geometry[0]);
        }

        double best = double.MaxValue;
        for (int i = 1; i < geometry.Count; i++)
        {
            double d = GeoMath.DistanceMetersToSegment(p, geometry[i - 1], geometry[i]);
            if (d < best)
            {
                best = d;
            }
        }

        return best;
    }

    private static BoundingBox ComputeBoundingBox(RoadGraph graph, IReadOnlyList<long> pathNodes)
    {
        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;

        foreach (var nodeId in pathNodes)
        {
            var p = graph.Coordinates[nodeId];
            if (p.Lat < minLat) minLat = p.Lat;
            if (p.Lat > maxLat) maxLat = p.Lat;
            if (p.Lon < minLon) minLon = p.Lon;
            if (p.Lon > maxLon) maxLon = p.Lon;
        }

        return new BoundingBox(minLat, maxLat, minLon, maxLon);
    }
}
