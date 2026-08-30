namespace TrainingRoutePlanner.Domain;

/// <summary>
/// Ein einzelner Trainingsschritt mit bereits aufgeloester Zielleistung und
/// Unterbrechungstoleranz - unabhaengig davon, ob er aus einer manuell gewaehlten
/// Zone oder einem importierten FIT-Workout stammt (siehe ZoneResolver).
/// </summary>
public sealed class TrainingStep
{
    public required TimeSpan Duration { get; init; }
    public required double TargetPowerWatts { get; init; }
    public required double MaxDisruptionScore { get; init; }
    public string? Label { get; init; }
}

public sealed class TrainingPlan
{
    public required IReadOnlyList<TrainingStep> Steps { get; init; }
}
