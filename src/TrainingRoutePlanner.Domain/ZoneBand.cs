namespace TrainingRoutePlanner.Domain;

/// <summary>
/// Unterbrechungstoleranz ist an das %FTP-Band gekoppelt, nicht an den Zonennamen -
/// siehe CONCEPT.md Abschnitt 3.2/3.4. Dadurch funktioniert das Scoring unabhaengig
/// davon, ob eine Zone manuell gewaehlt oder aus einer FIT-Datei mit rohem
/// Leistungsbereich uebernommen wurde. Werte sind Platzhalter, siehe Abschnitt 7.
/// </summary>
public sealed record ZoneBand(string Name, double FtpPercentLow, double FtpPercentHigh, double MaxDisruptionScore);

public static class ZoneBands
{
    public static readonly IReadOnlyList<ZoneBand> Default =
    [
        new ZoneBand("GA1", FtpPercentLow: 0, FtpPercentHigh: 75, MaxDisruptionScore: 50.0),
        new ZoneBand("GA2", FtpPercentLow: 75, FtpPercentHigh: 90, MaxDisruptionScore: 10.0),
        new ZoneBand("EB", FtpPercentLow: 90, FtpPercentHigh: 100, MaxDisruptionScore: 3.0),
        new ZoneBand("SB", FtpPercentLow: 100, FtpPercentHigh: 106, MaxDisruptionScore: 1.5),
        new ZoneBand("VO2max", FtpPercentLow: 106, FtpPercentHigh: double.MaxValue, MaxDisruptionScore: 1.0),
    ];

    /// <summary>Sprint ist nicht FTP-basiert (siehe RiderProfile.SprintAvgWatts) - eigener,
    /// fester Schwellwert, ebenso streng wie VO2max.</summary>
    public const double SprintMaxDisruptionScore = 1.0;

    public static ZoneBand ForFtpPercent(double ftpPercent) =>
        Default.FirstOrDefault(b => ftpPercent >= b.FtpPercentLow && ftpPercent < b.FtpPercentHigh)
        ?? Default[^1];

    public static ZoneBand ForZone(TrainingZone zone) =>
        Default.First(b => b.Name == zone.ToString());
}
