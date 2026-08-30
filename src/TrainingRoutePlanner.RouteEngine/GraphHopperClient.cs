using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.RouteEngine;

public sealed record GraphHopperRoute(double DistanceMeters, TimeSpan Time, IReadOnlyList<GeoPoint> Geometry);

/// <summary>Thin wrapper over the GraphHopper /route endpoint, see CONCEPT.md Abschnitt 4.2.
/// Requires the GraphHopper profile to run WITHOUT contraction hierarchies - round_trip is
/// incompatible with CH (validated in the Phase 0 spike, CONCEPT.md 6.1).</summary>
public interface IGraphHopperClient
{
    Task<GraphHopperRoute> RoundTripAsync(GeoPoint start, double distanceMeters, int seed, CancellationToken ct = default);

    Task<GraphHopperRoute> RouteThroughWaypointsAsync(IReadOnlyList<GeoPoint> waypoints, CancellationToken ct = default);
}

public sealed class GraphHopperClient(HttpClient http, string profile = "bike") : IGraphHopperClient
{
    public async Task<GraphHopperRoute> RoundTripAsync(GeoPoint start, double distanceMeters, int seed, CancellationToken ct = default)
    {
        var url = $"/route?point={Fmt(start)}&profile={profile}&algorithm=round_trip" +
                   $"&round_trip.distance={distanceMeters.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}" +
                   $"&round_trip.seed={seed}&points_encoded=false";
        return await GetRouteAsync(url, ct);
    }

    public async Task<GraphHopperRoute> RouteThroughWaypointsAsync(IReadOnlyList<GeoPoint> waypoints, CancellationToken ct = default)
    {
        if (waypoints.Count < 2)
            throw new ArgumentException("At least start and end waypoint required.", nameof(waypoints));

        var pointsQuery = string.Join("&", waypoints.Select(p => $"point={Fmt(p)}"));
        var url = $"/route?{pointsQuery}&profile={profile}&points_encoded=false";
        return await GetRouteAsync(url, ct);
    }

    private async Task<GraphHopperRoute> GetRouteAsync(string url, CancellationToken ct)
    {
        var response = await http.GetFromJsonAsync<GhRouteResponse>(url, JsonOptions, ct)
            ?? throw new GraphHopperException("Empty response from GraphHopper");

        if (response.Paths is null || response.Paths.Count == 0)
        {
            var message = response.Message ?? "GraphHopper returned no paths";
            throw new GraphHopperException(message);
        }

        var path = response.Paths[0];
        var geometry = path.Points.Coordinates
            .Select(c => new GeoPoint(c[1], c[0]))
            .ToList();

        return new GraphHopperRoute(path.Distance, TimeSpan.FromMilliseconds(path.Time), geometry);
    }

    private static string Fmt(GeoPoint p) =>
        $"{p.Lat.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
        $"{p.Lon.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

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
    }

    private sealed class GhPoints
    {
        public required List<double[]> Coordinates { get; set; }
    }
}

public sealed class GraphHopperException(string message) : Exception(message);
