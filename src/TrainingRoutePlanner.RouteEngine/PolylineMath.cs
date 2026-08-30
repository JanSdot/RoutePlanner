using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.RouteEngine;

internal static class PolylineMath
{
    private const double EarthRadiusMeters = 6_371_000;

    public static double HaversineMeters(GeoPoint a, GeoPoint b)
    {
        var p1 = a.Lat * Math.PI / 180.0;
        var p2 = b.Lat * Math.PI / 180.0;
        var dPhi = (b.Lat - a.Lat) * Math.PI / 180.0;
        var dLambda = (b.Lon - a.Lon) * Math.PI / 180.0;
        var h = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2) +
                Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Sqrt(h));
    }

    public static double TotalLengthMeters(IReadOnlyList<GeoPoint> geometry)
    {
        var total = 0.0;
        for (var i = 1; i < geometry.Count; i++)
            total += HaversineMeters(geometry[i - 1], geometry[i]);
        return total;
    }

    /// <summary>Punkt entlang der Geometrie bei gegebener kumulierter Distanz vom Start -
    /// wird genutzt, um die ungefaehre Position eines Trainingsschritts auf der groben
    /// Rundtour-Form zu finden, siehe CONCEPT.md Abschnitt 4.2.</summary>
    public static GeoPoint PointAtDistance(IReadOnlyList<GeoPoint> geometry, double targetDistanceMeters)
    {
        if (geometry.Count == 0)
            throw new ArgumentException("Geometry must not be empty.", nameof(geometry));
        if (geometry.Count == 1 || targetDistanceMeters <= 0)
            return geometry[0];

        var accumulated = 0.0;
        for (var i = 1; i < geometry.Count; i++)
        {
            var segmentLength = HaversineMeters(geometry[i - 1], geometry[i]);
            if (accumulated + segmentLength >= targetDistanceMeters)
            {
                var remaining = targetDistanceMeters - accumulated;
                var t = segmentLength <= 0 ? 0 : remaining / segmentLength;
                var a = geometry[i - 1];
                var b = geometry[i];
                return new GeoPoint(a.Lat + (b.Lat - a.Lat) * t, a.Lon + (b.Lon - a.Lon) * t);
            }
            accumulated += segmentLength;
        }
        return geometry[^1];
    }
}
