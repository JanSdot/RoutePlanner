namespace TrainingRoutePlanner.Domain;

public sealed class RouteRequest
{
    public required GeoPoint StartPoint { get; init; }
    public required TrainingPlan Plan { get; init; }
    public required RiderProfile Rider { get; init; }

    /// <summary>Gesamtbudget fuer Hin- und Rueckweg zusammen, siehe CONCEPT.md 4.4.</summary>
    public double MaxApproachMinutes { get; init; } = 30;

    public SegmentReusePreference SegmentReuse { get; init; } = SegmentReusePreference.PreferReuse;

    /// <summary>Wenn false: kein exaktes Wiederverwenden desselben Korridors fuer
    /// Wiederholungen desselben Trainingsschritts (das erzwingt sonst haeufig eine Kehrtwende,
    /// weil GraphHopper vom Korridorende zurueck zum -anfang denselben Weg zurueckroutet).
    /// Zusaetzlich wird die finale Route auf abrupte Richtungswechsel geprueft und ggf. mit
    /// einer Warnung gekennzeichnet - kann in duennen Strassennetzen nicht immer vollstaendig
    /// vermieden werden.</summary>
    public bool AllowUTurns { get; init; } = true;
}
