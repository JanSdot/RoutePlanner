namespace TrainingRoutePlanner.Data;

public enum ClubMembershipStatus { Pending, Approved }

/// <summary>Mitgliedschaft eines Nutzers in einem Verein - Beitritt braucht die Freigabe eines
/// Verantwortlichen (<see cref="IsAdmin"/>), siehe CONCEPT.md Phase-4-Backlog
/// "Mehrbenutzerfähigkeit/Auth/Vereine", Stufe 2. Ein Nutzer hat zu jedem Zeitpunkt hoechstens
/// EINE Zeile (pending oder approved) - siehe den eindeutigen Index auf <see cref="UserId"/> in
/// WattLoopDbContext.</summary>
public sealed class ClubMembership
{
    public required Guid Id { get; init; }
    public required Guid ClubId { get; init; }
    public required string UserId { get; init; }
    public required ClubMembershipStatus Status { get; set; }
    public required bool IsAdmin { get; set; }
    public required DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedByUserId { get; set; }
}
