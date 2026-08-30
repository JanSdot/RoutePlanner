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
                "surface": [ [0, 1, "asphalt"], [1, 3, "gravel"] ]
              }
            }
          ]
        }
        """;

    private sealed class FakeHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static GraphHopperClient CreateClient(string json = ResponseJson)
    {
        var http = new HttpClient(new FakeHandler(json)) { BaseAddress = new Uri("http://fake-graphhopper") };
        return new GraphHopperClient(http);
    }

    [Fact]
    public async Task RoundTripAsync_ParsesSurfaceSegmentsFromPathDetails()
    {
        var client = CreateClient();

        var route = await client.RoundTripAsync(new GeoPoint(52.50, 13.40), 400, seed: 1);

        Assert.Equal(2, route.SurfaceSegments.Count);

        Assert.Equal("asphalt", route.SurfaceSegments[0].Surface);
        Assert.Equal(2, route.SurfaceSegments[0].Geometry.Count); // Indizes 0..1 inklusive
        Assert.Equal(52.50, route.SurfaceSegments[0].Geometry[0].Lat, 3);

        Assert.Equal("gravel", route.SurfaceSegments[1].Surface);
        Assert.Equal(3, route.SurfaceSegments[1].Geometry.Count); // Indizes 1..3 inklusive
        Assert.Equal(52.51, route.SurfaceSegments[1].Geometry[0].Lat, 3);
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
        var client = CreateClient(json);

        var route = await client.RoundTripAsync(new GeoPoint(52.50, 13.40), 100, seed: 1);

        Assert.Empty(route.SurfaceSegments);
    }
}
