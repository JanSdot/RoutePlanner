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
}
