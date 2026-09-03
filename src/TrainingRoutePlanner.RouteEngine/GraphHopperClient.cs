using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.RouteEngine;

public sealed record GraphHopperRoute(
    double DistanceMeters,
    TimeSpan Time,
    IReadOnlyList<GeoPoint> Geometry,
    IReadOnlyList<SurfaceSegment> SurfaceSegments,
    IReadOnlyList<SurfaceSegment> SmoothnessSegments);

/// <summary>Thin wrapper over the GraphHopper /route endpoint, see CONCEPT.md Abschnitt 4.2.
/// Requires the GraphHopper profile to run WITHOUT contraction hierarchies - round_trip is
/// incompatible with CH (validated in the Phase 0 spike, CONCEPT.md 6.1). Nutzt POST mit
/// JSON-Body statt GET mit Query-String, seit blockierte Bereiche (CONCEPT.md 6.18) einen
/// per-Request custom_model brauchen - GraphHoppers klassischer "block_area"-Parameter wurde
/// entfernt (Serverantwort verweist explizit auf custom_model mit "areas").</summary>
public interface IGraphHopperClient
{
    Task<GraphHopperRoute> RoundTripAsync(
        GeoPoint start, double distanceMeters, int seed, IReadOnlyList<BlockedArea> blockedAreas,
        IReadOnlyList<ConstructionClosure>? constructionClosures = null, CancellationToken ct = default);

    Task<GraphHopperRoute> RouteThroughWaypointsAsync(
        IReadOnlyList<GeoPoint> waypoints, IReadOnlyList<BlockedArea> blockedAreas,
        IReadOnlyList<ConstructionClosure>? constructionClosures = null, CancellationToken ct = default);
}

public sealed class GraphHopperClient(HttpClient http, string profile = "bike") : IGraphHopperClient
{
    public async Task<GraphHopperRoute> RoundTripAsync(
        GeoPoint start, double distanceMeters, int seed, IReadOnlyList<BlockedArea> blockedAreas,
        IReadOnlyList<ConstructionClosure>? constructionClosures = null, CancellationToken ct = default)
    {
        var body = new GhRouteRequestBody
        {
            Points = [ToLonLat(start)],
            Profile = profile,
            Algorithm = "round_trip",
            RoundTripDistance = distanceMeters,
            RoundTripSeed = seed,
            CustomModel = BuildCustomModel(blockedAreas, constructionClosures ?? []),
        };
        return await PostRouteAsync(body, ct);
    }

    public async Task<GraphHopperRoute> RouteThroughWaypointsAsync(
        IReadOnlyList<GeoPoint> waypoints, IReadOnlyList<BlockedArea> blockedAreas,
        IReadOnlyList<ConstructionClosure>? constructionClosures = null, CancellationToken ct = default)
    {
        if (waypoints.Count < 2)
            throw new ArgumentException("At least start and end waypoint required.", nameof(waypoints));

        var body = new GhRouteRequestBody
        {
            Points = waypoints.Select(ToLonLat).ToList(),
            Profile = profile,
            CustomModel = BuildCustomModel(blockedAreas, constructionClosures ?? []),
        };
        return await PostRouteAsync(body, ct);
    }

    private async Task<GraphHopperRoute> PostRouteAsync(GhRouteRequestBody body, CancellationToken ct)
    {
        var httpResponse = await http.PostAsJsonAsync("/route", body, JsonOptions, ct);
        var response = await httpResponse.Content.ReadFromJsonAsync<GhRouteResponse>(JsonOptions, ct)
            ?? throw new GraphHopperException("Empty response from GraphHopper");

        if (response.Paths is null || response.Paths.Count == 0)
        {
            var message = response.Message ?? "GraphHopper returned no paths";
            throw new GraphHopperException(message);
        }

        var path = response.Paths[0];
        var geometry = path.Points.Coordinates
            .Select(c => new GeoPoint(c[1], c[0], c.Length > 2 ? c[2] : null))
            .ToList();
        var surfaceSegments = ParsePathDetailSegments(path.Details, geometry, "surface");
        var smoothnessSegments = ParsePathDetailSegments(path.Details, geometry, "smoothness");

        return new GraphHopperRoute(
            path.Distance, TimeSpan.FromMilliseconds(path.Time), geometry, surfaceSegments, smoothnessSegments);
    }

    // GraphHopper liefert die per "details=..." angeforderten Path Details als
    // [vonIndex, bisIndex, wert]-Tripel, die Indizes in path.Points.Coordinates. Fehlt der
    // angefragte Key ganz (z.B. Encoded-Value nicht in der GraphHopper-Config aktiv), gibt es
    // einfach keine Segmente statt eines Fehlers.
    private static List<SurfaceSegment> ParsePathDetailSegments(
        Dictionary<string, List<JsonElement>>? details, IReadOnlyList<GeoPoint> geometry, string detailKey)
    {
        var result = new List<SurfaceSegment>();
        if (details is null || !details.TryGetValue(detailKey, out var ranges))
            return result;

        foreach (var range in ranges)
        {
            var fromIndex = range[0].GetInt32();
            var toIndex = range[1].GetInt32();
            var value = range[2].GetString() ?? "unknown";
            result.Add(new SurfaceSegment
            {
                Surface = value,
                Geometry = geometry.Skip(fromIndex).Take(toIndex - fromIndex + 1).ToList(),
            });
        }
        return result;
    }

    // GraphHoppers GeoJSON-Konvention ist [lon, lat], entgegengesetzt zu unserem eigenen
    // GeoPoint(Lat, Lon) - hier zentral konvertiert statt an jeder Aufrufstelle einzeln.
    private static double[] ToLonLat(GeoPoint p) => [p.Lon, p.Lat];

    private const int BlockedAreaPolygonSides = 12;
    private const double MetersPerDegreeLat = 111_320.0;

    // Pufferbreite fuer Baustellen-Geometrien (Punkt ODER LineString) - siehe CONCEPT.md
    // Abschnitt 6.27 Recherche ("kleiner Radius, z.B. 15-20m"). Deckt eine typische
    // Strassenbreite plus etwas Toleranz ab, ohne bei dichten Stadt-Strassennetzen gleich
    // benachbarte Parallelstrassen mitzusperren.
    private const double ConstructionClosureBufferMeters = 18.0;

    private static GhCustomModel? BuildCustomModel(
        IReadOnlyList<BlockedArea> blockedAreas, IReadOnlyList<ConstructionClosure> constructionClosures)
    {
        if (blockedAreas.Count == 0 && constructionClosures.Count == 0)
            return null;

        var features = new List<GhAreaFeature>(blockedAreas.Count + constructionClosures.Count);
        var areaIds = new List<string>(blockedAreas.Count + constructionClosures.Count);
        for (var i = 0; i < blockedAreas.Count; i++)
        {
            var id = $"blocked{i}";
            areaIds.Add(id);
            features.Add(new GhAreaFeature
            {
                Id = id,
                Geometry = new GhPolygonGeometry
                {
                    Coordinates = [BuildCirclePolygon(blockedAreas[i].Center, blockedAreas[i].RadiusMeters)],
                },
            });
        }

        // Beide Sperrgrade (Full/Directional) werden identisch behandelt - siehe
        // ClosureSeverity-Dokumentation (Domain) fuer die Begruendung. Es gibt daher keine
        // separate multiply_by-Abstufung nach Severity, nur eine gemeinsame "meiden"-Regel wie
        // bei BlockedArea.
        for (var i = 0; i < constructionClosures.Count; i++)
        {
            var id = $"construction{i}";
            areaIds.Add(id);
            features.Add(new GhAreaFeature
            {
                Id = id,
                Geometry = new GhPolygonGeometry { Coordinates = [BuildClosurePolygon(constructionClosures[i])] },
            });
        }

        return new GhCustomModel
        {
            Priority = [new GhPriorityRule
            {
                If = string.Join(" || ", areaIds.Select(id => $"in_{id}")),
                MultiplyBy = "0",
            }],
            Areas = new GhAreas { Features = features },
        };
    }

    // Naeherung des Kreises als 12-Ecks-Polygon um einen Mittelpunkt - flache
    // Grad/Meter-Umrechnung reicht fuer die hier relevanten kleinen Radien (Zehner- bis
    // niedrige Hunderter-Meter-Bereich) locker aus, echte Geodaesie waere hier ueberdimensioniert.
    // Gemeinsam genutzt von BlockedArea-Kreisen UND punktfoermigen Baustellen-Geometrien.
    private static List<double[]> BuildCirclePolygon(GeoPoint center, double radiusMeters)
    {
        var latRad = center.Lat * Math.PI / 180.0;
        var ring = new List<double[]>(BlockedAreaPolygonSides + 1);
        for (var i = 0; i <= BlockedAreaPolygonSides; i++)
        {
            var angle = 2 * Math.PI * (i % BlockedAreaPolygonSides) / BlockedAreaPolygonSides;
            var latOffsetDeg = radiusMeters * Math.Cos(angle) / MetersPerDegreeLat;
            var lonOffsetDeg = radiusMeters * Math.Sin(angle) / (MetersPerDegreeLat * Math.Cos(latRad));
            ring.Add([center.Lon + lonOffsetDeg, center.Lat + latOffsetDeg]);
        }
        return ring;
    }

    // Punktfoermige Baustellen (nur ein einzelnes GeoJSON-Point-Feature, kein LineString in der
    // GeometryCollection) werden wie ein BlockedArea-Kreis behandelt. Baustellen MIT
    // LineString-Geometrie werden stattdessen entlang der Strasse gepuffert (siehe
    // BuildLineBufferPolygon) - praeziser als ein einzelner Kreis um irgendeinen Punkt der
    // Baustelle, siehe CONCEPT.md 6.27 Recherche.
    private static List<double[]> BuildClosurePolygon(ConstructionClosure closure) =>
        closure.Geometry.Count == 1
            ? BuildCirclePolygon(closure.Geometry[0], ConstructionClosureBufferMeters)
            : BuildLineBufferPolygon(closure.Geometry, ConstructionClosureBufferMeters);

    // Puffert eine Punktfolge (LineString) zu einem Polygon: pro Vertex ein senkrechter
    // Off-set nach links/rechts (lokale, auf den ersten Punkt bezogene Meter-Koordinaten,
    // dieselbe flache Naeherung wie BuildCirclePolygon), linke Offsets in Reihenfolge gefolgt
    // von rechten Offsets in umgekehrter Reihenfolge ergeben einen geschlossenen Ring ("Wurst"-
    // Form entlang der Strasse). Bei stark gewinkelten Polylinien mit vielen Vertices koennen
    // sich benachbarte Segmente an konkaven Knicken theoretisch leicht ueberlappen - fuer die
    // hier typischen kurzen Baustellen-Linien (wenige Vertices, siehe Recherche-Stichprobe) ist
    // das genauso vernachlaessigbar wie die 12-Eck-Kreis-Naeherung bei BlockedArea.
    private static List<double[]> BuildLineBufferPolygon(IReadOnlyList<GeoPoint> line, double radiusMeters)
    {
        var origin = line[0];
        var metersPerDegreeLon = MetersPerDegreeLat * Math.Cos(origin.Lat * Math.PI / 180.0);

        var xy = line.Select(p => (
            X: (p.Lon - origin.Lon) * metersPerDegreeLon,
            Y: (p.Lat - origin.Lat) * MetersPerDegreeLat)).ToList();

        var left = new List<(double X, double Y)>(xy.Count);
        var right = new List<(double X, double Y)>(xy.Count);
        for (var i = 0; i < xy.Count; i++)
        {
            // Segmentrichtung: nachfolgender Punkt, oder (am letzten Vertex) der vorhergehende -
            // ergibt an jedem Vertex eine wohldefinierte lokale Richtung ohne Sonderfall fuer
            // den allerersten/letzten Punkt.
            var (dx, dy) = i < xy.Count - 1
                ? (xy[i + 1].X - xy[i].X, xy[i + 1].Y - xy[i].Y)
                : (xy[i].X - xy[i - 1].X, xy[i].Y - xy[i - 1].Y);
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 0) { dx = 1; dy = 0; length = 1; }
            var normalX = -dy / length;
            var normalY = dx / length;
            left.Add((xy[i].X + normalX * radiusMeters, xy[i].Y + normalY * radiusMeters));
            right.Add((xy[i].X - normalX * radiusMeters, xy[i].Y - normalY * radiusMeters));
        }

        var ring = new List<double[]>(left.Count + right.Count + 1);
        foreach (var p in left)
            ring.Add(ToLonLatFromLocalMeters(p, origin, metersPerDegreeLon));
        for (var i = right.Count - 1; i >= 0; i--)
            ring.Add(ToLonLatFromLocalMeters(right[i], origin, metersPerDegreeLon));
        ring.Add(ring[0]); // Ring schliessen, wie bei BuildCirclePolygon.
        return ring;
    }

    private static double[] ToLonLatFromLocalMeters((double X, double Y) local, GeoPoint origin, double metersPerDegreeLon) =>
        [origin.Lon + local.X / metersPerDegreeLon, origin.Lat + local.Y / MetersPerDegreeLat];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class GhRouteRequestBody
    {
        public required List<double[]> Points { get; set; }
        public required string Profile { get; set; }
        public bool PointsEncoded { get; set; } = false;
        public bool Elevation { get; set; } = true;
        public List<string> Details { get; set; } = ["surface", "smoothness"];
        public string? Algorithm { get; set; }

        [JsonPropertyName("round_trip.distance")]
        public double? RoundTripDistance { get; set; }

        [JsonPropertyName("round_trip.seed")]
        public int? RoundTripSeed { get; set; }

        public GhCustomModel? CustomModel { get; set; }
    }

    private sealed class GhCustomModel
    {
        public required List<GhPriorityRule> Priority { get; set; }
        public required GhAreas Areas { get; set; }
    }

    private sealed class GhPriorityRule
    {
        public required string If { get; set; }
        public required string MultiplyBy { get; set; }
    }

    private sealed class GhAreas
    {
        public string Type { get; set; } = "FeatureCollection";
        public required List<GhAreaFeature> Features { get; set; }
    }

    private sealed class GhAreaFeature
    {
        public string Type { get; set; } = "Feature";
        public required string Id { get; set; }
        public required GhPolygonGeometry Geometry { get; set; }
    }

    private sealed class GhPolygonGeometry
    {
        public string Type { get; set; } = "Polygon";
        public required List<List<double[]>> Coordinates { get; set; }
    }

    private sealed class GhRouteResponse
    {
        public List<GhPath>? Paths { get; set; }
        public string? Message { get; set; }
    }

    private sealed class GhPath
    {
        public double Distance { get; set; }
        public double Time { get; set; }
        public required GhPoints Points { get; set; }
        public Dictionary<string, List<JsonElement>>? Details { get; set; }
    }

    private sealed class GhPoints
    {
        public required List<double[]> Coordinates { get; set; }
    }
}

public sealed class GraphHopperException(string message) : Exception(message);
