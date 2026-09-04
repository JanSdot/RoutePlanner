namespace TrainingRoutePlanner.Data;

public enum ClubStatus { Pending, Approved }

/// <summary>Ein Verein, dem Nutzer beitreten koennen (CONCEPT.md Phase-4-Backlog
/// "Mehrbenutzerfähigkeit/Auth/Vereine", Stufe 2). Verantwortliche (Admins) werden nicht hier,
/// sondern ueber <see cref="ClubMembership.IsAdmin"/> abgebildet - ein Verein kann mehrere
/// Verantwortliche haben. Ein neu gegruendeter Verein selbst braucht zusaetzlich zur
/// Mitgliedschaft des Gruenders noch die Freigabe eines Plattform-Administrators (siehe
/// Program.cs /admin/clubs/*), bevor andere ihn ueberhaupt sehen/ihm beitreten koennen.</summary>
public sealed class Club
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required ClubStatus Status { get; set; }
}
