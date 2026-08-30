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

/// <summary>Ein Abschnitt der finalen Route mit einheitlichem Wert eines GraphHopper
/// <c>path_details</c>-Merkmals (OSM <c>surface</c> ODER <c>smoothness</c>-Tag), fuer die
/// Kartenanzeige - siehe CONCEPT.md Abschnitt 6.8. Unabhaengig von <see cref="RouteSegment"/>:
/// deckt die GESAMTE Route lueckenlos ab (nicht nur Trainings-Intervalle). Anders als der
/// urspruengliche Kommentar hier nahelegte, ist das inzwischen KEIN reines Anzeige-Feature mehr:
/// RouteConstructionService.EvaluateUnpavedSurfaces nutzt sowohl Oberflaechen- als auch
/// Smoothness-Segmente auch fuer die Untergrund-Vermeidungs-Grenzwerte (siehe CONCEPT.md 6.19).</summary>
public sealed class SurfaceSegment
{
    public required string Surface { get; init; }
    public required IReadOnlyList<GeoPoint> Geometry { get; init; }
}

public sealed class RouteResult
{
    public required IReadOnlyList<GeoPoint> Geometry { get; init; }
    public required double TotalDistanceMeters { get; init; }
    public required TimeSpan EstimatedTotalTime { get; init; }
    public required IReadOnlyList<RouteWarning> Warnings { get; init; }
    public required IReadOnlyList<RouteSegment> Segments { get; init; }
    public required IReadOnlyList<SurfaceSegment> SurfaceSegments { get; init; }

    /// <summary>Wie SurfaceSegments, aber ueber das <c>smoothness</c>-Tag statt <c>surface</c> -
    /// siehe SurfaceClassifier.IsBadSmoothness und CONCEPT.md 6.19. Nicht fuer die Kartenanzeige
    /// gedacht (kein Frontend-Feature dafuer), nur Eingang in die Untergrund-Vermeidung.</summary>
    public required IReadOnlyList<SurfaceSegment> SmoothnessSegments { get; init; }

    /// <summary>Die fuer die Zeitschaetzung tatsaechlich verwendeten Windbedingungen, falls
    /// RouteRequest.PlannedStartTime gesetzt UND eine Vorhersage verfuegbar war - sonst null
    /// (siehe CONCEPT.md Phase-4-Backlog "Windmodellierung"). Rein informativ fuer die
    /// Anzeige, macht die Basis der Zeitschaetzung fuer den Nutzer nachvollziehbar.</summary>
    public WindConditions? Wind { get; init; }
}
