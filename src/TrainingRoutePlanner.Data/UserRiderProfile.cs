namespace TrainingRoutePlanner.Data;

/// <summary>Gespeichertes Fahrerprofil (FTP/Gewicht/Sprint-Watt) pro Nutzerkonto - erster
/// konkreter Nutzen eines eingeloggten Zustands (siehe CONCEPT.md Phase-4-Backlog
/// "Mehrbenutzerfähigkeit/Auth/Vereine"). 1:1 zu AspNetUsers ueber UserId, daher kein eigener
/// Primary Key noetig - UserId selbst ist der Primary Key (siehe WattLoopDbContext).</summary>
public sealed class UserRiderProfile
{
    public required string UserId { get; init; }
    public required double FtpWatts { get; set; }
    public required double WeightKg { get; set; }
    public required double SprintAvgWatts { get; set; }
}
