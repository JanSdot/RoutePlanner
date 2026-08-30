using System.Xml.Linq;
using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.RouteEngine;
using Xunit;

namespace TrainingRoutePlanner.Tests;

public class GpxWriterTests
{
    [Fact]
    public void ToGpx_ProducesValidGpxWithTrackPointsAndElevation()
    {
        var result = new RouteResult
        {
            Geometry =
            [
                new GeoPoint(52.5, 13.4, 40.0),
                new GeoPoint(52.51, 13.41, 42.5),
                new GeoPoint(52.52, 13.42, null),
            ],
            TotalDistanceMeters = 1500,
            EstimatedTotalTime = TimeSpan.FromMinutes(5),
            Warnings = [],
            Segments = [],
            SurfaceSegments = [],
            SmoothnessSegments = [],
        };

        var gpx = GpxWriter.ToGpx(result, "Test Route");

        var doc = XDocument.Parse(gpx);
        XNamespace ns = "http://www.topografix.com/GPX/1/1";

        var trkpts = doc.Descendants(ns + "trkpt").ToList();
        Assert.Equal(3, trkpts.Count);
        Assert.Equal("52.500000", trkpts[0].Attribute("lat")!.Value);
        Assert.Equal("13.400000", trkpts[0].Attribute("lon")!.Value);
        Assert.Equal("40.0", trkpts[0].Element(ns + "ele")!.Value);
        Assert.Null(trkpts[2].Element(ns + "ele"));

        Assert.Equal("Test Route", doc.Descendants(ns + "name").First().Value);
    }

    [Fact]
    public void ToGpx_WritesNamedWaypointsForSegmentStartAndEnd()
    {
        var result = new RouteResult
        {
            Geometry = [new GeoPoint(52.5, 13.4), new GeoPoint(52.6, 13.5)],
            TotalDistanceMeters = 15000,
            EstimatedTotalTime = TimeSpan.FromMinutes(30),
            Warnings = [],
            Segments =
            [
                new RouteSegment
                {
                    Label = "Work",
                    Geometry = [new GeoPoint(52.51, 13.41, 40.0), new GeoPoint(52.52, 13.42, 45.0)],
                },
                new RouteSegment
                {
                    Label = "Work",
                    Geometry = [new GeoPoint(52.55, 13.45), new GeoPoint(52.56, 13.46)],
                },
            ],
            SurfaceSegments = [],
            SmoothnessSegments = [],
        };

        var gpx = GpxWriter.ToGpx(result);

        var doc = XDocument.Parse(gpx);
        XNamespace ns = "http://www.topografix.com/GPX/1/1";

        var waypoints = doc.Root!.Elements(ns + "wpt").ToList();
        Assert.Equal(4, waypoints.Count);

        var names = waypoints.Select(w => w.Element(ns + "name")!.Value).ToList();
        Assert.Equal(["Start: Work (1)", "Ende: Work (1)", "Start: Work (2)", "Ende: Work (2)"], names);

        Assert.Equal("52.510000", waypoints[0].Attribute("lat")!.Value);
        Assert.Equal("40.0", waypoints[0].Element(ns + "ele")!.Value);

        // wpt* muss laut GPX-1.1-Schema vor trk* stehen.
        var allTopLevel = doc.Root!.Elements().ToList();
        var lastWptIndex = allTopLevel.FindLastIndex(e => e.Name == ns + "wpt");
        var firstTrkIndex = allTopLevel.FindIndex(e => e.Name == ns + "trk");
        Assert.True(lastWptIndex < firstTrkIndex);
    }
}
