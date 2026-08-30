namespace TrainingRoutePlanner.Domain;

/// <summary>Ein Kandidaten-Korridor aus der Vorberechnung (siehe CONCEPT.md Abschnitt 4.1),
/// bereits auf die tatsaechlich benoetigte Teilstrecke zugeschnitten.</summary>
public sealed class Corridor
{
    public required GeoPoint Start { get; init; }
    public required GeoPoint End { get; init; }
    public required double LengthMeters { get; init; }
    public required double DisruptionScore { get; init; }
    public required IReadOnlyList<GeoPoint> Geometry { get; init; }
}
