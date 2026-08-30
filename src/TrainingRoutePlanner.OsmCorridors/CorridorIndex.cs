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

    private readonly record struct BoundingBox(double MinLat, double MaxLat, double MinLon, double MaxLon);

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
    }

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
