namespace TrainingRoutePlanner.Domain;

/// <summary>
/// Siehe CONCEPT.md Abschnitt 3.1 und 3.3. Defaults entsprechen typischen
/// Rennrad-Annahmen und sind fuer fortgeschrittene Nutzer ueberschreibbar.
/// </summary>
public sealed class RiderProfile
{
    public required double FtpWatts { get; init; }
    public required double WeightKg { get; init; }

    /// <summary>Sprint ist nicht FTP-basiert (neuromuskulaerer Kurzzeit-Ausbruch), siehe 3.2.</summary>
    public required double SprintAvgWatts { get; init; }

    public double DragAreaCdA { get; init; } = 0.35;
    public double RollingResistanceCoefficient { get; init; } = 0.005;
    public double DrivetrainEfficiency { get; init; } = 0.97;
}
