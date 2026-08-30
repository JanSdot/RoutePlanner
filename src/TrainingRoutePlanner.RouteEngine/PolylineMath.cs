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

    /// <summary>Projiziert einen Punkt auf die naeheste Stelle der Route und gibt die
    /// kumulierte Distanz vom Routenanfang bis zu dieser Projektion zurueck - genutzt, um
    /// Pflicht-Wegpunkte (RouteRequest.RequiredPoints) in der richtigen Reihenfolge zwischen
    /// die Korridor-Wegpunkte einzusortieren (siehe RouteConstructionService, CONCEPT.md 6.19).
    /// Flache Grad/Meter-Naeherung reicht fuer die kurzen Segmentlaengen einer
    /// Rundtour-Grobform voellig aus (wie schon bei BlockedArea-Kreisen in GraphHopperClient).</summary>
    public static double NearestPointDistanceAlongMeters(IReadOnlyList<GeoPoint> geometry, GeoPoint point)
    {
        if (geometry.Count < 2)
            return 0.0;

        var bestDistanceAlong = 0.0;
        var bestDistanceSq = double.MaxValue;
        var accumulated = 0.0;
        var metersPerDegreeLat = 111_320.0;
        var metersPerDegreeLon = metersPerDegreeLat * Math.Cos(point.Lat * Math.PI / 180.0);

        for (var i = 1; i < geometry.Count; i++)
        {
            var a = geometry[i - 1];
            var b = geometry[i];
            var segmentLength = HaversineMeters(a, b);

            // Lokale Meter-Koordinaten relativ zum Zielpunkt (numerisch stabiler als absolute
            // Lat/Lon-Werte und fuer diese kurzen Distanzen ausreichend genau).
            var ax = (a.Lon - point.Lon) * metersPerDegreeLon;
            var ay = (a.Lat - point.Lat) * metersPerDegreeLat;
            var bx = (b.Lon - point.Lon) * metersPerDegreeLon;
            var by = (b.Lat - point.Lat) * metersPerDegreeLat;
            var abx = bx - ax;
            var aby = by - ay;
            var abLengthSq = abx * abx + aby * aby;
            var t = abLengthSq <= 0 ? 0.0 : Math.Clamp((-ax * abx - ay * aby) / abLengthSq, 0.0, 1.0);
            var closestX = ax + t * abx;
            var closestY = ay + t * aby;
            var distanceSq = closestX * closestX + closestY * closestY;

            if (distanceSq < bestDistanceSq)
            {
                bestDistanceSq = distanceSq;
                bestDistanceAlong = accumulated + t * segmentLength;
            }
            accumulated += segmentLength;
        }
        return bestDistanceAlong;
    }

    public static double BearingDegrees(GeoPoint from, GeoPoint to)
    {
        var phi1 = from.Lat * Math.PI / 180.0;
        var phi2 = to.Lat * Math.PI / 180.0;
        var dLambda = (to.Lon - from.Lon) * Math.PI / 180.0;
        var x = Math.Sin(dLambda) * Math.Cos(phi2);
        var y = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda);
        var bearing = Math.Atan2(x, y) * 180.0 / Math.PI;
        return (bearing + 360.0) % 360.0;
    }

    private static double AngleDiffDegrees(double a, double b)
    {
        var diff = Math.Abs(a - b) % 360.0;
        return Math.Min(diff, 360.0 - diff);
    }

    /// <summary>Findet Stellen entlang der Route, an denen sich die Fahrtrichtung abrupt
    /// umkehrt (naeherungsweise eine Kehrtwende) - vergleicht die Peilung kurz vor und kurz
    /// nach jedem Punkt. Siehe CONCEPT.md: Nutzereinstellung "keine Kehrtwenden", die das nicht
    /// in jedem Fall algorithmisch verhindern kann (z.B. echte Sackgassen), aber zumindest
    /// transparent anzeigen soll.</summary>
    public static List<GeoPoint> DetectSharpReversals(
        IReadOnlyList<GeoPoint> geometry, double windowMeters = 40, double angleThresholdDegrees = 150)
    {
        var result = new List<GeoPoint>();
        if (geometry.Count < 3) return result;

        var totalLength = TotalLengthMeters(geometry);
        var cumulative = new double[geometry.Count];
        for (var i = 1; i < geometry.Count; i++)
            cumulative[i] = cumulative[i - 1] + HaversineMeters(geometry[i - 1], geometry[i]);

        double lastDetectionDistance = double.NegativeInfinity;
        for (var i = 1; i < geometry.Count - 1; i++)
        {
            var beforeDist = Math.Max(0, cumulative[i] - windowMeters);
            var afterDist = Math.Min(totalLength, cumulative[i] + windowMeters);
            var before = PointAtDistance(geometry, beforeDist);
            var after = PointAtDistance(geometry, afterDist);
            if (HaversineMeters(before, geometry[i]) < 1 || HaversineMeters(after, geometry[i]) < 1)
                continue; // zu nah an Routenanfang/-ende fuer ein sinnvolles Fenster

            var bearingIn = BearingDegrees(before, geometry[i]);
            var bearingOut = BearingDegrees(geometry[i], after);
            if (AngleDiffDegrees(bearingIn, bearingOut) < angleThresholdDegrees)
                continue;

            // Eine einzelne Kehrtwende schlaegt ueber mehrere benachbarte Punkte hinweg an -
            // nur den ersten Treffer je zusammenhaengendem Bereich behalten.
            if (cumulative[i] - lastDetectionDistance < windowMeters * 2)
                continue;

            result.Add(geometry[i]);
            lastDetectionDistance = cumulative[i];
        }
        return result;
    }
}
