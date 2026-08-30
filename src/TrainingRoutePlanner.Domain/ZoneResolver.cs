namespace TrainingRoutePlanner.Domain;

/// <summary>Loest Ziel-Watt und Unterbrechungsschwelle fuer einen Trainingsschritt auf -
/// aus einer manuell gewaehlten Zone, einem rohen %FTP-Bereich (FIT-Import) oder
/// absoluten Watt (FIT-Import bzw. Sprint). Siehe CONCEPT.md Abschnitt 3.2/3.4.</summary>
public static class ZoneResolver
{
    public static TrainingStep FromZone(TrainingZone zone, TimeSpan duration, RiderProfile profile)
    {
        if (zone == TrainingZone.Sprint)
        {
            return new TrainingStep
            {
                Duration = duration,
                TargetPowerWatts = profile.SprintAvgWatts,
                MaxDisruptionScore = ZoneBands.SprintMaxDisruptionScore,
                Label = nameof(TrainingZone.Sprint),
            };
        }

        var band = ZoneBands.ForZone(zone);
        var upperBound = double.IsPositiveInfinity(band.FtpPercentHigh) || band.FtpPercentHigh == double.MaxValue
            ? band.FtpPercentLow + 15
            : band.FtpPercentHigh;
        var midFtpPercent = (band.FtpPercentLow + upperBound) / 2.0;
        return FromFtpPercent(midFtpPercent, duration, profile, band.Name);
    }

    public static TrainingStep FromFtpPercent(double ftpPercent, TimeSpan duration, RiderProfile profile, string? label = null)
    {
        var watts = profile.FtpWatts * ftpPercent / 100.0;
        var band = ZoneBands.ForFtpPercent(ftpPercent);
        return new TrainingStep
        {
            Duration = duration,
            TargetPowerWatts = watts,
            MaxDisruptionScore = band.MaxDisruptionScore,
            Label = label ?? band.Name,
        };
    }

    public static TrainingStep FromAbsoluteWatts(double watts, TimeSpan duration, RiderProfile profile, string? label = null)
    {
        var ftpPercent = watts / profile.FtpWatts * 100.0;
        var band = ZoneBands.ForFtpPercent(ftpPercent);
        return new TrainingStep
        {
            Duration = duration,
            TargetPowerWatts = watts,
            MaxDisruptionScore = band.MaxDisruptionScore,
            Label = label ?? band.Name,
        };
    }
}
