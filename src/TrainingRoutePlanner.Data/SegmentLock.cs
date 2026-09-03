namespace TrainingRoutePlanner.Data;

public enum SegmentLockStatus { Active, Pending, Rejected }

/// <summary>Eine dauerhaft gespeicherte Sperrung eines Streckenabschnitts (Kreis um Lat/Lon) -
/// die persistierte Ergaenzung zu den rein Request-lokalen BlockedAreas (Domain), siehe
/// CONCEPT.md Phase-4-Backlog "Mehrbenutzerfähigkeit/Auth/Vereine", Stufe 3. Deckt BEIDE
/// dauerhaften Sperr-Arten in einer Tabelle ab: <see cref="ClubId"/> null = persoenliche Sperre
/// (sofort <see cref="SegmentLockStatus.Active"/>, keine Freigabe noetig), <see cref="ClubId"/>
/// gesetzt = Vereins-Sperre (startet <see cref="SegmentLockStatus.Pending"/>, wird erst nach
/// Freigabe durch einen Verantwortlichen des Vereins aktiv und gilt dann fuer alle
/// Vereinsmitglieder).</summary>
public sealed class SegmentLock
{
    public required Guid Id { get; init; }
    public required string OwnerUserId { get; init; }
    public Guid? ClubId { get; init; }
    public required double Lat { get; init; }
    public required double Lon { get; init; }
    public required double RadiusMeters { get; init; }
    public required SegmentLockStatus Status { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedByUserId { get; set; }
}
