namespace TrainingRoutePlanner.Domain;

/// <summary>Transparenz-Hinweis nach der Fallback-Eskalation aus CONCEPT.md Abschnitt 4.3 -
/// z.B. wenn fuer einen Trainingsschritt kein perfekter Korridor gefunden wurde.</summary>
public sealed class RouteWarning
{
    public required string Message { get; init; }
    public GeoPoint? Location { get; init; }
}

/// <summary>Ein dedizierter Korridor-Abschnitt fuer einen Effort-Trainingsschritt (siehe
/// CONCEPT.md Abschnitt 4.2) - fuer die Kartenanzeige, damit Nutzer die Intervalle aus dem
/// Trainingsplan auf der Route wiedererkennen koennen.</summary>
public sealed class RouteSegment
{
    public required string Label { get; init; }
    public required IReadOnlyList<GeoPoint> Geometry { get; init; }
}

public sealed class RouteResult
{
    public required IReadOnlyList<GeoPoint> Geometry { get; init; }
    public required double TotalDistanceMeters { get; init; }
    public required TimeSpan EstimatedTotalTime { get; init; }
    public required IReadOnlyList<RouteWarning> Warnings { get; init; }
    public required IReadOnlyList<RouteSegment> Segments { get; init; }
}
