namespace TrainingRoutePlanner.Domain;

public sealed class RouteRequest
{
    public required GeoPoint StartPoint { get; init; }
    public required TrainingPlan Plan { get; init; }
    public required RiderProfile Rider { get; init; }

    /// <summary>Gesamtbudget fuer Hin- und Rueckweg zusammen, siehe CONCEPT.md 4.4.</summary>
    public double MaxApproachMinutes { get; init; } = 30;

    public SegmentReusePreference SegmentReuse { get; init; } = SegmentReusePreference.PreferReuse;
}
