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
    /// Rundtour-Form zu finden, siehe CONCEPT.md Abschnitt 4.2. Interpoliert auch die
    /// Elevation (falls vorhanden), fuer die hoehenprofil-adjustierte Distanzverfeinerung
    /// aus Abschnitt 3.3/6.2.</summary>
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
                double? elevation = a.Elevation.HasValue && b.Elevation.HasValue
                    ? a.Elevation.Value + (b.Elevation.Value - a.Elevation.Value) * t
                    : null;
                return new GeoPoint(a.Lat + (b.Lat - a.Lat) * t, a.Lon + (b.Lon - a.Lon) * t, elevation);
            }
            accumulated += segmentLength;
        }
        return geometry[^1];
    }

    /// <summary>Mittlere Steigung (Aufstieg/Distanz) ueber ein Fenster von windowMeters,
    /// zentriert auf centerDistanceMeters entlang der Geometrie - grobe, aber fuer die
    /// iterative Verfeinerung ausreichende Naeherung (siehe CONCEPT.md 3.3/6.2). Gibt 0.0
    /// zurueck, wenn keine Elevation-Daten vorliegen (z.B. GraphHopper ohne Elevation-Support).</summary>
    public static double AverageGradient(IReadOnlyList<GeoPoint> geometry, double centerDistanceMeters, double windowMeters)
    {
        var totalLength = TotalLengthMeters(geometry);
        var from = Math.Max(0, centerDistanceMeters - windowMeters / 2);
        var to = Math.Min(totalLength, centerDistanceMeters + windowMeters / 2);
        if (to <= from)
            return 0.0;

        var start = PointAtDistance(geometry, from);
        var end = PointAtDistance(geometry, to);
        if (!start.Elevation.HasValue || !end.Elevation.HasValue)
            return 0.0;

        var horizontalDistance = HaversineMeters(start, end);
        if (horizontalDistance <= 0)
            return 0.0;

        return (end.Elevation.Value - start.Elevation.Value) / horizontalDistance;
    }
}
