using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.OsmCorridors;

/// <summary>Geodesy helpers ported 1:1 from phase0-spike/scripts/corridor_check.py
/// (haversine_m, bearing_deg, angle_diff) so distances/bearings match the validated
/// Python reference exactly.</summary>
internal static class GeoMath
{
    private const double EarthRadiusMeters = 6_371_000;

    internal static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

    internal static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = DegreesToRadians(lat1);
        double p2 = DegreesToRadians(lat2);
        double dPhi = DegreesToRadians(lat2 - lat1);
        double dLambda = DegreesToRadians(lon2 - lon1);
        double a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
                   + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Sqrt(a));
    }

    internal static double HaversineMeters(GeoPoint a, GeoPoint b) =>
        HaversineMeters(a.Lat, a.Lon, b.Lat, b.Lon);

    internal static double BearingDegrees(GeoPoint from, GeoPoint to)
    {
        double phi1 = DegreesToRadians(from.Lat);
        double phi2 = DegreesToRadians(to.Lat);
        double dLambda = DegreesToRadians(to.Lon - from.Lon);
        double x = Math.Sin(dLambda) * Math.Cos(phi2);
        double y = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda);
        double degrees = RadiansToDegrees(Math.Atan2(x, y));
        return ((degrees % 360) + 360) % 360;
    }

    internal static double AngleDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return Math.Min(d, 360 - d);
    }

    /// <summary>Minimum distance in meters from a point to a line segment, via a local
    /// equirectangular projection centered on the query point. Accurate enough at
    /// corridor scale (single-digit km); not meant for long-range geodesy.</summary>
    internal static double DistanceMetersToSegment(GeoPoint p, GeoPoint segA, GeoPoint segB)
    {
        const double metersPerDegLat = 111_320.0;
        double metersPerDegLon = metersPerDegLat * Math.Cos(DegreesToRadians(p.Lat));

        (double x, double y) Project(GeoPoint q) =>
            ((q.Lon - p.Lon) * metersPerDegLon, (q.Lat - p.Lat) * metersPerDegLat);

        var a = Project(segA);
        var b = Project(segB);

        double abx = b.x - a.x, aby = b.y - a.y;
        double apx = -a.x, apy = -a.y; // p projects to the origin by construction
        double abLenSq = abx * abx + aby * aby;
        double t = abLenSq <= 1e-9 ? 0.0 : Math.Clamp((apx * abx + apy * aby) / abLenSq, 0.0, 1.0);
        double cx = a.x + t * abx, cy = a.y + t * aby;
        double dx = -cx, dy = -cy;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
