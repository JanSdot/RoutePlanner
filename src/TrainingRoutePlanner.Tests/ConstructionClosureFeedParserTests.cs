using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.RouteEngine;
using Xunit;

namespace TrainingRoutePlanner.Tests;

public class ConstructionClosureFeedParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseVizFeed_FullClosure_WithLineStringInGeometryCollection_PrefersLineStringGeometry()
    {
        var json = """
            {
              "features": [
                {
                  "properties": {
                    "id": "1/2026",
                    "street": "Beispielstraße",
                    "severity": "Vollsperrung",
                    "validity": { "from": "2026-01-01T00:00", "to": "2026-12-31T23:59" }
                  },
                  "geometry": {
                    "type": "GeometryCollection",
                    "geometries": [
                      { "type": "Point", "coordinates": [13.40, 52.50] },
                      { "type": "LineString", "coordinates": [[13.40, 52.50], [13.41, 52.51]] }
                    ]
                  }
                }
              ]
            }
            """;

        var result = ConstructionClosureFeedParser.ParseVizFeed(json, Now);

        var closure = Assert.Single(result);
        Assert.Equal("1/2026", closure.Id);
        Assert.Equal("Beispielstraße", closure.Street);
        Assert.Equal(ClosureSeverity.Full, closure.Severity);
        Assert.Equal(2, closure.Geometry.Count);
        Assert.Equal(52.50, closure.Geometry[0].Lat, 6);
        Assert.Equal(13.40, closure.Geometry[0].Lon, 6);
    }

    [Fact]
    public void ParseVizFeed_DirectionalClosure_PointOnlyGeometry_YieldsSinglePoint()
    {
        var json = """
            {
              "features": [
                {
                  "properties": {
                    "id": "2/2026",
                    "street": "Andere Straße",
                    "severity": "Fahrtrichtungssperrung",
                    "validity": { "from": "2026-01-01T00:00", "to": "2026-12-31T23:59" }
                  },
                  "geometry": { "type": "Point", "coordinates": [13.50, 52.55] }
                }
              ]
            }
            """;

        var result = ConstructionClosureFeedParser.ParseVizFeed(json, Now);

        var closure = Assert.Single(result);
        Assert.Equal(ClosureSeverity.Directional, closure.Severity);
        var point = Assert.Single(closure.Geometry);
        Assert.Equal(52.55, point.Lat, 6);
        Assert.Equal(13.50, point.Lon, 6);
    }

    [Fact]
    public void ParseVizFeed_NoClosureSeverity_IsFilteredOut()
    {
        var json = """
            {
              "features": [
                {
                  "properties": {
                    "id": "3/2026",
                    "street": "Ruhige Straße",
                    "severity": "keine Sperrung",
                    "validity": { "from": "2026-01-01T00:00", "to": "2026-12-31T23:59" }
                  },
                  "geometry": { "type": "Point", "coordinates": [13.50, 52.55] }
                }
              ]
            }
            """;

        var result = ConstructionClosureFeedParser.ParseVizFeed(json, Now);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseVizFeed_ValidityWindowExpiredBeforeNow_IsFilteredOut()
    {
        var json = """
            {
              "features": [
                {
                  "properties": {
                    "id": "4/2026",
                    "street": "Laengst fertige Baustelle",
                    "severity": "Vollsperrung",
                    "validity": { "from": "2020-01-01T00:00", "to": "2020-06-30T23:59" }
                  },
                  "geometry": { "type": "Point", "coordinates": [13.50, 52.55] }
                }
              ]
            }
            """;

        var result = ConstructionClosureFeedParser.ParseVizFeed(json, Now);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseVizFeed_ValidityWindowNotYetStarted_IsFilteredOut()
    {
        var json = """
            {
              "features": [
                {
                  "properties": {
                    "id": "5/2026",
                    "street": "Zukuenftige Baustelle",
                    "severity": "Vollsperrung",
                    "validity": { "from": "2027-01-01T00:00", "to": "2027-06-30T23:59" }
                  },
                  "geometry": { "type": "Point", "coordinates": [13.50, 52.55] }
                }
              ]
            }
            """;

        var result = ConstructionClosureFeedParser.ParseVizFeed(json, Now);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseVizFeed_MissingValidityTo_TreatedAsOpenEndedAndStaysActive()
    {
        // In der Recherche-Stichprobe kommt ein fehlendes "to" tatsaechlich vor (offene
        // Baustelle) - siehe ConstructionClosureFeedParser.ParseDate-Dokumentation.
        var json = """
            {
              "features": [
                {
                  "properties": {
                    "id": "6/2026",
                    "street": "Offene Baustelle",
                    "severity": "Vollsperrung",
                    "validity": { "from": "2025-01-01T00:00", "to": null }
                  },
                  "geometry": { "type": "Point", "coordinates": [13.50, 52.55] }
                }
              ]
            }
            """;

        var result = ConstructionClosureFeedParser.ParseVizFeed(json, Now);

        Assert.Single(result);
    }

    [Fact]
    public void ParseTicFallbackFeed_NullSeverity_IsConservativelyTreatedAsDirectional()
    {
        // tic.json liefert in der Praxis nie ein severity-Feld (siehe Klassen-Dokumentation) -
        // dennoch soll der Fallback nicht komplett wirkungslos sein.
        var json = """
            {
              "features": [
                {
                  "properties": {
                    "id": "LMS-1",
                    "street": "Tic-Straße",
                    "severity": null,
                    "validity": { "from": "", "to": "31.12.2026 23:59" }
                  },
                  "geometry": { "type": "Point", "coordinates": [13.50, 52.55] }
                }
              ]
            }
            """;

        var result = ConstructionClosureFeedParser.ParseTicFallbackFeed(json, Now);

        var closure = Assert.Single(result);
        Assert.Equal(ClosureSeverity.Directional, closure.Severity);
        Assert.Null(closure.ValidFrom);
        Assert.NotNull(closure.ValidTo);
    }

    [Fact]
    public void ParseTicFallbackFeed_GermanDateFormat_ExpiredEntry_IsFilteredOut()
    {
        var json = """
            {
              "features": [
                {
                  "properties": {
                    "id": "LMS-2",
                    "street": "Abgelaufene Tic-Baustelle",
                    "severity": null,
                    "validity": { "from": "", "to": "01.01.2020 00:00" }
                  },
                  "geometry": { "type": "Point", "coordinates": [13.50, 52.55] }
                }
              ]
            }
            """;

        var result = ConstructionClosureFeedParser.ParseTicFallbackFeed(json, Now);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseVizFeed_MissingId_IsFilteredOut()
    {
        var json = """
            {
              "features": [
                {
                  "properties": {
                    "street": "Ohne ID",
                    "severity": "Vollsperrung",
                    "validity": { "from": "2026-01-01T00:00", "to": "2026-12-31T23:59" }
                  },
                  "geometry": { "type": "Point", "coordinates": [13.50, 52.55] }
                }
              ]
            }
            """;

        var result = ConstructionClosureFeedParser.ParseVizFeed(json, Now);

        Assert.Empty(result);
    }
}
