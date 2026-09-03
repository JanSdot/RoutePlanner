namespace TrainingRoutePlanner.Data;

/// <summary>Ein Verein, dem Nutzer beitreten koennen (CONCEPT.md Phase-4-Backlog
/// "Mehrbenutzerfähigkeit/Auth/Vereine", Stufe 2). Verantwortliche (Admins) werden nicht hier,
/// sondern ueber <see cref="ClubMembership.IsAdmin"/> abgebildet - ein Verein kann mehrere
/// Verantwortliche haben.</summary>
public sealed class Club
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
}
