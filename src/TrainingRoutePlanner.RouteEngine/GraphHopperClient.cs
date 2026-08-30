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
        GeoPoint start, double distanceMeters, int seed, IReadOnlyList<BlockedArea> blockedAreas, CancellationToken ct = default);

    Task<GraphHopperRoute> RouteThroughWaypointsAsync(
        IReadOnlyList<GeoPoint> waypoints, IReadOnlyList<BlockedArea> blockedAreas, CancellationToken ct = default);
}

public sealed class GraphHopperClient(HttpClient http, string profile = "bike") : IGraphHopperClient
{
    public async Task<GraphHopperRoute> RoundTripAsync(
        GeoPoint start, double distanceMeters, int seed, IReadOnlyList<BlockedArea> blockedAreas, CancellationToken ct = default)
    {
        var body = new GhRouteRequestBody
        {
            Points = [ToLonLat(start)],
            Profile = profile,
            Algorithm = "round_trip",
            RoundTripDistance = distanceMeters,
            RoundTripSeed = seed,
            CustomModel = BuildCustomModel(blockedAreas),
        };
        return await PostRouteAsync(body, ct);
    }

    public async Task<GraphHopperRoute> RouteThroughWaypointsAsync(
        IReadOnlyList<GeoPoint> waypoints, IReadOnlyList<BlockedArea> blockedAreas, CancellationToken ct = default)
    {
        if (waypoints.Count < 2)
            throw new ArgumentException("At least start and end waypoint required.", nameof(waypoints));

        var body = new GhRouteRequestBody
        {
            Points = waypoints.Select(ToLonLat).ToList(),
            Profile = profile,
            CustomModel = BuildCustomModel(blockedAreas),
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

    private static GhCustomModel? BuildCustomModel(IReadOnlyList<BlockedArea> blockedAreas)
    {
        if (blockedAreas.Count == 0)
            return null;

        var features = new List<GhAreaFeature>(blockedAreas.Count);
        var areaIds = new List<string>(blockedAreas.Count);
        for (var i = 0; i < blockedAreas.Count; i++)
        {
            var id = $"blocked{i}";
            areaIds.Add(id);
            features.Add(new GhAreaFeature
            {
                Id = id,
                Geometry = new GhPolygonGeometry { Coordinates = [BuildCirclePolygon(blockedAreas[i])] },
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

    // Naeherung des Kreises als 12-Ecks-Polygon um BlockedArea.Center - flache
    // Grad/Meter-Umrechnung reicht fuer die hier relevanten kleinen Radien (Zehner- bis
    // niedrige Hunderter-Meter-Bereich) locker aus, echte Geodaesie waere hier ueberdimensioniert.
    private static List<double[]> BuildCirclePolygon(BlockedArea area)
    {
        var latRad = area.Center.Lat * Math.PI / 180.0;
        var ring = new List<double[]>(BlockedAreaPolygonSides + 1);
        for (var i = 0; i <= BlockedAreaPolygonSides; i++)
        {
            var angle = 2 * Math.PI * (i % BlockedAreaPolygonSides) / BlockedAreaPolygonSides;
            var latOffsetDeg = area.RadiusMeters * Math.Cos(angle) / MetersPerDegreeLat;
            var lonOffsetDeg = area.RadiusMeters * Math.Sin(angle) / (MetersPerDegreeLat * Math.Cos(latRad));
            ring.Add([area.Center.Lon + lonOffsetDeg, area.Center.Lat + latOffsetDeg]);
        }
        return ring;
    }

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
