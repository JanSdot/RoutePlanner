using System.Net;
using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.RouteEngine;
using Xunit;

namespace TrainingRoutePlanner.Tests;

public class GraphHopperClientTests
{
    // GraphHopper path_details: [vonIndex, bisIndex, wert]-Tripel je Detail-Typ, Indizes in
    // path.points.coordinates - siehe GraphHopperClient.ParseSurfaceSegments.
    private const string ResponseJson = """
        {
          "paths": [
            {
              "distance": 400.0,
              "time": 60000,
              "points": { "coordinates": [
                [13.40, 52.50, 10.0],
                [13.41, 52.51, 11.0],
                [13.42, 52.52, 12.0],
                [13.43, 52.53, 13.0]
              ] },
              "details": {
                "surface": [ [0, 1, "asphalt"], [1, 3, "gravel"] ],
                "smoothness": [ [0, 3, "good"] ]
              }
            }
          ]
        }
        """;

    private sealed class FakeHandler(string json) : HttpMessageHandler
    {
        // Merkt sich den zuletzt gesendeten Request-Body, damit Tests pruefen koennen, was
        // GraphHopperClient tatsaechlich an GraphHopper geschickt hat (z.B. das custom_model
        // fuer blockierte Bereiche).
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (GraphHopperClient Client, FakeHandler Handler) CreateClient(string json = ResponseJson)
    {
        var handler = new FakeHandler(json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://fake-graphhopper") };
        return (new GraphHopperClient(http), handler);
    }

    [Fact]
    public async Task RoundTripAsync_ParsesSurfaceSegmentsFromPathDetails()
    {
        var (client, _) = CreateClient();

        var route = await client.RoundTripAsync(new GeoPoint(52.50, 13.40), 400, seed: 1, blockedAreas: []);

        Assert.Equal(2, route.SurfaceSegments.Count);

        Assert.Equal("asphalt", route.SurfaceSegments[0].Surface);
        Assert.Equal(2, route.SurfaceSegments[0].Geometry.Count); // Indizes 0..1 inklusive
        Assert.Equal(52.50, route.SurfaceSegments[0].Geometry[0].Lat, 3);

        Assert.Equal("gravel", route.SurfaceSegments[1].Surface);
        Assert.Equal(3, route.SurfaceSegments[1].Geometry.Count); // Indizes 1..3 inklusive
        Assert.Equal(52.51, route.SurfaceSegments[1].Geometry[0].Lat, 3);
    }

    [Fact]
    public async Task RoundTripAsync_ParsesSmoothnessSegmentsFromPathDetails()
    {
        var (client, _) = CreateClient();

        var route = await client.RoundTripAsync(new GeoPoint(52.50, 13.40), 400, seed: 1, blockedAreas: []);

        var segment = Assert.Single(route.SmoothnessSegments);
        Assert.Equal("good", segment.Surface);
        Assert.Equal(4, segment.Geometry.Count); // Indizes 0..3 inklusive
    }

    [Fact]
    public async Task RoundTripAsync_MissingDetailsProducesNoSurfaceSegments()
    {
        const string json = """
            {
              "paths": [
                {
                  "distance": 100.0,
                  "time": 10000,
                  "points": { "coordinates": [[13.40, 52.50], [13.41, 52.51]] }
                }
              ]
            }
            """;
        var (client, _) = CreateClient(json);

        var route = await client.RoundTripAsync(new GeoPoint(52.50, 13.40), 100, seed: 1, blockedAreas: []);

        Assert.Empty(route.SurfaceSegments);
    }

    [Fact]
    public async Task RoundTripAsync_NoBlockedAreas_OmitsCustomModelFromRequest()
    {
        var (client, handler) = CreateClient();

        await client.RoundTripAsync(new GeoPoint(52.50, 13.40), 400, seed: 1, blockedAreas: []);

        Assert.DoesNotContain("custom_model", handler.LastRequestBody);
    }

    [Fact]
    public async Task RoundTripAsync_RequestsBothSurfaceAndSmoothnessPathDetails()
    {
        var (client, handler) = CreateClient();

        await client.RoundTripAsync(new GeoPoint(52.50, 13.40), 400, seed: 1, blockedAreas: []);

        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var details = doc.RootElement.GetProperty("details").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("surface", details);
        Assert.Contains("smoothness", details);
    }

    [Fact]
    public async Task RoundTripAsync_BlockedAreas_SendsCustomModelWithAreaPolygonAndExclusionRule()
    {
        var (client, handler) = CreateClient();
        var blocked = new BlockedArea(new GeoPoint(52.50, 13.40), RadiusMeters: 50);

        await client.RoundTripAsync(new GeoPoint(52.50, 13.40), 400, seed: 1, blockedAreas: [blocked]);

        Assert.NotNull(handler.LastRequestBody);
        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var customModel = doc.RootElement.GetProperty("custom_model");

        var priorityRule = customModel.GetProperty("priority")[0];
        Assert.Equal("in_blocked0", priorityRule.GetProperty("if").GetString());
        Assert.Equal("0", priorityRule.GetProperty("multiply_by").GetString());

        var feature = customModel.GetProperty("areas").GetProperty("features")[0];
        Assert.Equal("blocked0", feature.GetProperty("id").GetString());
        var ring = feature.GetProperty("geometry").GetProperty("coordinates")[0];
        // Geschlossener Ring: erster und letzter Punkt identisch, mindestens ein paar Ecken
        // fuer eine brauchbare Kreis-Naeherung.
        Assert.True(ring.GetArrayLength() >= 5);
        var first = ring[0];
        var last = ring[ring.GetArrayLength() - 1];
        Assert.Equal(first[0].GetDouble(), last[0].GetDouble(), 6);
        Assert.Equal(first[1].GetDouble(), last[1].GetDouble(), 6);
    }
}
