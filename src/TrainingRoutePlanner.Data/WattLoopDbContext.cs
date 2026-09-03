using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TrainingRoutePlanner.Data;

/// <summary>Persistenz-Grundlage fuer Nutzerkonten (CONCEPT.md Phase-4-Backlog
/// "Mehrbenutzerfähigkeit/Auth/Vereine"). Nutzt ASP.NET Core Identity's Standard-EF-Core-Schema
/// (IdentityUser/IdentityRole - Rollen bleiben ungenutzt, siehe Club/ClubMembership fuer das
/// eigene, leichtgewichtige Verantwortlichen-Modell), erweitert um UserRiderProfile (Stufe 1,
/// 1:1 pro Nutzer), Club/ClubMembership (Stufe 2) und SegmentLock (Stufe 3, persistierte
/// Sperr-Bereiche).</summary>
public sealed class WattLoopDbContext(DbContextOptions<WattLoopDbContext> options)
    : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<UserRiderProfile> UserRiderProfiles => Set<UserRiderProfile>();
    public DbSet<Club> Clubs => Set<Club>();
    public DbSet<ClubMembership> ClubMemberships => Set<ClubMembership>();
    public DbSet<SegmentLock> SegmentLocks => Set<SegmentLock>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<UserRiderProfile>().HasKey(p => p.UserId);

        // Hoechstens eine Mitgliedschaft (pending oder approved) pro Nutzer gleichzeitig - siehe
        // CONCEPT.md Phase-4-Backlog, Annahme "ein Nutzer ist zu jedem Zeitpunkt Mitglied in
        // hoechstens einem Verein".
        builder.Entity<ClubMembership>().HasIndex(m => m.UserId).IsUnique();

        // Fuer die Freigabe-Warteschlangen-Abfrage ("alle Pending-Sperren eines Vereins").
        builder.Entity<SegmentLock>().HasIndex(s => new { s.ClubId, s.Status });
    }
}
