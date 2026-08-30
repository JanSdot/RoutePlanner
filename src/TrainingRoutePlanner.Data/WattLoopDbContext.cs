using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TrainingRoutePlanner.Data;

/// <summary>Persistenz-Grundlage fuer Nutzerkonten (CONCEPT.md Phase-4-Backlog
/// "Mehrbenutzerfähigkeit/Auth/Vereine", Stufe 1). Nutzt ASP.NET Core Identity's Standard-
/// EF-Core-Schema (IdentityUser/IdentityRole), erweitert um UserRiderProfile (1:1 pro Nutzer) -
/// Vereine/Mitgliedschaft (Stufe 2) und persistierte Sperr-Bereiche (Stufe 3) kommen als eigene
/// Entitaeten/Migrationen dazu, sobald diese Stufen umgesetzt werden.</summary>
public sealed class WattLoopDbContext(DbContextOptions<WattLoopDbContext> options)
    : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<UserRiderProfile> UserRiderProfiles => Set<UserRiderProfile>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<UserRiderProfile>().HasKey(p => p.UserId);
    }
}
