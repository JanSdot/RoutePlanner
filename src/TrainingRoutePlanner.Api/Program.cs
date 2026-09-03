using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using TrainingRoutePlanner.Data;
using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.FitParsing;
using TrainingRoutePlanner.OsmCorridors;
using TrainingRoutePlanner.PowerModel;
using TrainingRoutePlanner.RouteEngine;
using TrainingRoutePlanner.Api;

// Lokal liefert `neon link`/`neon env pull` die DB-Verbindung in eine .env.local am Repo-Root
// (siehe .neon-Skill-Setup) - .NET liest .env-Dateien nicht selbst, Render setzt DATABASE_URL
// in Produktion dagegen direkt als echte Prozess-Umgebungsvariable (kein .env-Datei-Handling
// noetig, dort existiert auch gar keine .env.local - sie ist gitignored). Sucht von der
// Build-Output-Directory nach oben statt eine feste Verzeichnistiefe anzunehmen, damit es
// unabhaengig von Debug/Release- oder RID-spezifischem Output-Pfad funktioniert.
var searchDir = new DirectoryInfo(AppContext.BaseDirectory);
for (var i = 0; i < 10 && searchDir is not null; i++, searchDir = searchDir.Parent)
{
    var candidate = Path.Combine(searchDir.FullName, ".env.local");
    if (File.Exists(candidate))
    {
        DotNetEnv.Env.Load(candidate);
        break;
    }
}

var builder = WebApplication.CreateBuilder(args);

// Render (und die meisten Container-Hoster) geben den Port ueber die Umgebungsvariable PORT
// vor, statt eine feste appsettings.json-Portnummer zuzulassen. Lokal bleibt launchSettings.json
// unberuehrt, da PORT dort nicht gesetzt ist.
var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(renderPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
}

builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

const string FrontendCorsPolicy = "FrontendDevServer";
builder.Services.AddCors(options =>
{
    // Phase 2 MVP: kein Auth/Multi-User noetig (siehe CONCEPT.md Abschnitt 6 Phase 2).
    // Origin per Konfiguration (appsettings/Umgebungsvariable Cors__AllowedOrigin), damit
    // sich lokaler Vite-Dev-Server UND ein deployter Frontend-Host beide eintragen lassen,
    // ohne Code zu aendern.
    var allowedOrigin = builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:5173";
    options.AddPolicy(FrontendCorsPolicy, policy =>
        policy.WithOrigins(allowedOrigin).AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddHttpClient<IGraphHopperClient, GraphHopperClient>((sp, http) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    // Lokal: volle URL in appsettings.json (GraphHopper:BaseUrl). Auf Render: der GraphHopper-
    // Service liegt im privaten Netzwerk, render.yaml gibt dafuer per fromService nur "host:port"
    // (GraphHopper:Host) durch, kein Schema - Render-internes Netzwerk ist ohnehin nur HTTP.
    var baseUrl = config["GraphHopper:BaseUrl"];
    if (string.IsNullOrEmpty(baseUrl))
    {
        var host = config["GraphHopper:Host"]
            ?? throw new InvalidOperationException("Weder GraphHopper:BaseUrl noch GraphHopper:Host sind konfiguriert.");
        baseUrl = $"http://{host}";
    }
    http.BaseAddress = new Uri(baseUrl);
});

// Open-Meteo: kostenlose, schluessellose Wetter-API fuer die windbewusste Zeitschaetzung (siehe
// CONCEPT.md Phase-4-Backlog "Windmodellierung") - kein Konfigurationseintrag noetig, die
// Basis-URL ist oeffentlich und fest.
builder.Services.AddHttpClient<IWindForecastClient, OpenMeteoWindForecastClient>(http =>
{
    http.BaseAddress = new Uri("https://api.open-meteo.com");
});

// VIZ Berlin: offizieller, kostenloser, schluesselloser GeoJSON-Feed der Berliner
// Verkehrsinformationszentrale fuer aktuelle Baustellen/Sperrungen (siehe CONCEPT.md
// Abschnitt 6.27 - deckt nur Berlin ab, siehe dortige Recherche + Abschnitt 7). Wird von einem
// Hintergrund-Dienst stuendlich abgerufen (ConstructionClosureRefreshService), nicht pro
// Nutzer-Request.
builder.Services.AddHttpClient<IConstructionClosureFeedClient, VizBerlinConstructionClosureClient>(http =>
{
    http.BaseAddress = new Uri("https://api.viz.berlin.de");
});
builder.Services.AddSingleton<ConstructionClosureCache>();
builder.Services.AddSingleton<IConstructionClosureCache>(sp => sp.GetRequiredService<ConstructionClosureCache>());
builder.Services.AddHostedService<ConstructionClosureRefreshService>();

// CorridorIndex.Load ist teuer (PBF-Parse + Korridor-Extraktion) - laeuft einmalig beim
// Start, siehe CONCEPT.md 4.1 ("einmalig pro Region, cachebar"). Fuer Phase 1 eine feste
// Region (Sportforum Berlin, 60km) statt Multi-Region-Verwaltung.
builder.Services.AddSingleton<ICorridorIndex>(sp =>
{
    var pbfPath = sp.GetRequiredService<IConfiguration>()["OsmCorridors:PbfPath"]
        ?? throw new InvalidOperationException("OsmCorridors:PbfPath fehlt in der Konfiguration.");
    return CorridorIndex.Load(pbfPath);
});

builder.Services.AddSingleton<PowerSpeedModel>();
builder.Services.AddSingleton<FitWorkoutParser>();
builder.Services.AddScoped<RouteConstructionService>();

// Neon liefert DATABASE_URL im Standard-Postgres-URI-Format (postgresql://user:pass@host/db?
// sslmode=require&...), Npgsql erwartet aber sein eigenes keyword=value-Format - daher die
// Umwandlung via NpgsqlConnectionStringBuilder statt Npgsql's URI-String direkt zu uebergeben.
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? throw new InvalidOperationException("Umgebungsvariable DATABASE_URL fehlt (Neon-Connection-String).");
builder.Services.AddDbContext<WattLoopDbContext>(options =>
    options.UseNpgsql(ToNpgsqlConnectionString(databaseUrl)));

builder.Services.AddIdentityCore<IdentityUser>(options =>
{
    // Ohne das koennten zwei Konten dieselbe E-Mail-Adresse tragen - bei einem
    // E-Mail-basierten Login (siehe /auth/login) waere das mehrdeutig. Identity's Default ist
    // false, weil Identity urspruenglich Username-basiertes Login als Grundfall annimmt.
    options.User.RequireUniqueEmail = true;
})
    .AddEntityFrameworkStores<WattLoopDbContext>();

// Reines Bearer-Token-Login statt Cookies: Frontend (SPA) und API laufen auf unterschiedlichen
// Origins (siehe FrontendCorsPolicy) - Cross-Site-Cookies bräuchten SameSite=None + genaues
// Domain-Handling, ein Bearer-Token im Authorization-Header ist fuer dieses Setup deutlich
// simpler UND ist bereits von AllowAnyHeader() in der CORS-Policy abgedeckt.
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException(
        "Jwt:SigningKey fehlt (lokal per 'dotnet user-secrets set Jwt:SigningKey ...', " +
        "auf Render per Umgebungsvariable Jwt__SigningKey).");
var jwtSigningKeyBytes = Encoding.UTF8.GetBytes(jwtSigningKey);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwtSigningKeyBytes),
            ValidateLifetime = true,
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Neon-URI (postgresql://user:pass@host/db?sslmode=require&channel_binding=require) in Npgsql's
// eigenes Verbindungsstring-Format uebersetzt - ueber den staerker typisierten
// NpgsqlConnectionStringBuilder statt String-Konkatenation, damit z.B. Sonderzeichen im
// Passwort (URL-kodiert in der Neon-URI) korrekt behandelt werden.
static string ToNpgsqlConnectionString(string postgresUrl)
{
    var uri = new Uri(postgresUrl);
    var userInfo = uri.UserInfo.Split(':', 2);
    var queryParams = uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""), StringComparer.OrdinalIgnoreCase);

    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Database = uri.AbsolutePath.TrimStart('/'),
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
    };
    if (queryParams.TryGetValue("sslmode", out var sslMode))
        builder.SslMode = Enum.Parse<SslMode>(sslMode, ignoreCase: true);
    if (queryParams.TryGetValue("channel_binding", out var channelBinding))
        builder.ChannelBinding = Enum.Parse<ChannelBinding>(channelBinding, ignoreCase: true);

    return builder.ConnectionString;
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok());

// Ampeln/Stoppschilder fuer den optionalen Kartenlayer (CONCEPT.md 6.21) - einmalig geladen,
// gesamte Region auf einmal statt bounding-box-basiert, konsistent mit CorridorIndex' Ansatz
// einer einzigen fest im Speicher gehaltenen Region (siehe CONCEPT.md 4.1).
app.MapGet("/junctions", (ICorridorIndex corridorIndex) => Results.Ok(corridorIndex.GetAllJunctions()))
    .RequireAuthorization()
    .WithName("GetJunctions");

// Aktuell aktive Baustellen-Sperrungen (VIZ Berlin, siehe CONCEPT.md Abschnitt 6.27) - fuer den
// Kartenlayer UND die Sidebar-Liste im Frontend (dort mit "Ignorieren fuer diese Route"-Button
// je Eintrag, siehe /route unten). Liest direkt aus dem stuendlich aktualisierten Cache, kein
// Live-Abruf pro Request.
app.MapGet("/construction-closures", (IConstructionClosureCache closureCache) => Results.Ok(closureCache.GetActive()))
    .RequireAuthorization()
    .WithName("GetConstructionClosures");

// Render terminiert TLS an seinem eigenen Edge und leitet intern per HTTP weiter - ein
// erzwungenes Redirect hier wuerde ohne Forwarded-Header-Auswertung ins Leere laufen.
// Lokal (Development) bleibt es wie gehabt aktiv.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseCors(FrontendCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();

// App ist bewusst oeffentlich ohne Auth (siehe DEPLOY.md), soll aber nicht in Suchmaschinen
// auftauchen und keine automatisierten Crawler/Scraper bedienen. X-Robots-Tag ist das
// API-Aequivalent zum <meta name="robots"> im Frontend (dort zusaetzlich robots.txt). Die
// User-Agent-Sperre erwischt nur Bots, die sich ehrlich identifizieren - kein Ersatz fuer
// echten Bot-Schutz (Rate-Limiting/WAF), aber haelt bekannte SEO-/KI-Crawler ohne echten
// Aufwand fern.
var blockedUserAgentSubstrings = new[]
{
    "bot", "spider", "crawl", "slurp", "scrape", "archive.org_bot", "ccbot", "gptbot",
    "claudebot", "google-extended", "bytespider", "petalbot", "semrushbot", "ahrefsbot",
    "mj12bot", "dotbot", "yandex",
};
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    var userAgent = context.Request.Headers.UserAgent.ToString();
    if (blockedUserAgentSubstrings.Any(s => userAgent.Contains(s, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

// E-Mail dient direkt als Identity-Username (kein separater Anzeigename noetig fuer Stufe 1
// des Konten-Features, siehe CONCEPT.md Phase-4-Backlog "Mehrbenutzerfähigkeit/Auth/Vereine").
app.MapPost("/auth/register", async (RegisterRequest request, UserManager<IdentityUser> userManager) =>
{
    var user = new IdentityUser { UserName = request.Email, Email = request.Email };
    var result = await userManager.CreateAsync(user, request.Password);
    if (!result.Succeeded)
        return Results.BadRequest(result.Errors.Select(e => e.Description));
    return Results.Ok();
})
.WithName("Register");

app.MapPost("/auth/login", async (LoginRequest request, UserManager<IdentityUser> userManager) =>
{
    var user = await userManager.FindByEmailAsync(request.Email);
    if (user is null || !await userManager.CheckPasswordAsync(user, request.Password))
        return Results.Unauthorized();

    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id),
        new Claim(ClaimTypes.Email, user.Email!),
    };
    var credentials = new SigningCredentials(new SymmetricSecurityKey(jwtSigningKeyBytes), SecurityAlgorithms.HmacSha256);
    // 30 Tage bewusst ohne Refresh-Token-Mechanismus - fuer den aktuellen Umfang (Stufe 1,
    // reines E-Mail/Passwort-Login) reicht ein simples langlebiges Token, ein Refresh-Flow waere
    // hier verfrueht (siehe CONCEPT.md: Stufe 2/3 kommen erst noch).
    var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddDays(30), signingCredentials: credentials);
    return Results.Ok(new AuthResponse(new JwtSecurityTokenHandler().WriteToken(token), user.Email!));
})
.WithName("Login");

app.MapGet("/auth/me", (ClaimsPrincipal principal) =>
    Results.Ok(new { email = principal.FindFirstValue(ClaimTypes.Email) }))
    .RequireAuthorization()
    .WithName("Me");

// Gespeichertes Fahrerprofil (FTP/Gewicht/Sprint-Watt) pro Nutzerkonto - erster konkreter Nutzen
// eines eingeloggten Zustands, siehe CONCEPT.md Phase-4-Backlog "Mehrbenutzerfähigkeit/Auth/
// Vereine". 1:1 pro Nutzer (UserId ist Primary Key, siehe UserRiderProfile), daher upsert statt
// separater Create/Update-Unterscheidung.
app.MapGet("/profile", async (ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var profile = await db.UserRiderProfiles.FindAsync(userId);
    return profile is null
        ? Results.NotFound()
        : Results.Ok(new RiderProfileDto(profile.FtpWatts, profile.WeightKg, profile.SprintAvgWatts));
})
.RequireAuthorization()
.WithName("GetProfile");

app.MapPut("/profile", async (RiderProfileDto request, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var profile = await db.UserRiderProfiles.FindAsync(userId);
    if (profile is null)
    {
        db.UserRiderProfiles.Add(new UserRiderProfile
        {
            UserId = userId,
            FtpWatts = request.FtpWatts,
            WeightKg = request.WeightKg,
            SprintAvgWatts = request.SprintAvgWatts,
        });
    }
    else
    {
        profile.FtpWatts = request.FtpWatts;
        profile.WeightKg = request.WeightKg;
        profile.SprintAvgWatts = request.SprintAvgWatts;
    }
    await db.SaveChangesAsync();
    return Results.Ok();
})
.RequireAuthorization()
.WithName("SaveProfile");

// Verein-Verantwortlicher: approved Mitgliedschaft MIT IsAdmin=true. Ein Verein kann mehrere
// Verantwortliche haben (Nutzer-Entscheidung), daher kein Alleinstellungsmerkmal wie "Ersteller".
async Task<bool> IsClubAdminAsync(WattLoopDbContext db, Guid clubId, string userId) =>
    await db.ClubMemberships.AnyAsync(m =>
        m.ClubId == clubId && m.UserId == userId && m.Status == ClubMembershipStatus.Approved && m.IsAdmin);

// Vereine (CONCEPT.md Phase-4-Backlog "Mehrbenutzerfähigkeit/Auth/Vereine", Stufe 2) - ein
// Nutzer ist zu jedem Zeitpunkt Mitglied/Anwaerter in hoechstens einem Verein (siehe den
// eindeutigen Index auf ClubMembership.UserId), Beitritt braucht die Freigabe eines
// Verantwortlichen.
app.MapPost("/clubs", async (CreateClubRequest request, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest("Name darf nicht leer sein.");

    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    if (await db.ClubMemberships.AnyAsync(m => m.UserId == userId))
        return Results.Conflict("Du bist bereits Mitglied oder Anwärter eines Vereins.");

    var club = new Club { Id = Guid.NewGuid(), Name = request.Name.Trim(), CreatedAt = DateTimeOffset.UtcNow };
    db.Clubs.Add(club);
    // Der Ersteller wird sofort Verantwortlicher - ohne diesen Schritt gaebe es keinen einzigen
    // Verantwortlichen, der jemals einen Beitritt freigeben koennte.
    db.ClubMemberships.Add(new ClubMembership
    {
        Id = Guid.NewGuid(),
        ClubId = club.Id,
        UserId = userId,
        Status = ClubMembershipStatus.Approved,
        IsAdmin = true,
        RequestedAt = DateTimeOffset.UtcNow,
        DecidedAt = DateTimeOffset.UtcNow,
        DecidedByUserId = userId,
    });
    await db.SaveChangesAsync();
    return Results.Ok(new ClubDto(club.Id, club.Name, MemberCount: 1));
})
.RequireAuthorization()
.WithName("CreateClub");

app.MapGet("/clubs", async (WattLoopDbContext db) =>
{
    var clubs = await db.Clubs
        .Select(c => new ClubDto(c.Id, c.Name, db.ClubMemberships.Count(m => m.ClubId == c.Id && m.Status == ClubMembershipStatus.Approved)))
        .ToListAsync();
    return Results.Ok(clubs);
})
.RequireAuthorization()
.WithName("ListClubs");

app.MapGet("/clubs/mine", async (ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var membership = await db.ClubMemberships.FirstOrDefaultAsync(m => m.UserId == userId);
    if (membership is null)
        return Results.Ok((ClubMembershipDto?)null);

    var club = await db.Clubs.FindAsync(membership.ClubId);
    return Results.Ok(new ClubMembershipDto(membership.ClubId, club!.Name, membership.Status.ToString(), membership.IsAdmin));
})
.RequireAuthorization()
.WithName("MyClubMembership");

app.MapPost("/clubs/{clubId:guid}/join", async (Guid clubId, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    if (await db.ClubMemberships.AnyAsync(m => m.UserId == userId))
        return Results.Conflict("Du bist bereits Mitglied oder Anwärter eines Vereins.");
    if (!await db.Clubs.AnyAsync(c => c.Id == clubId))
        return Results.NotFound();

    db.ClubMemberships.Add(new ClubMembership
    {
        Id = Guid.NewGuid(),
        ClubId = clubId,
        UserId = userId,
        Status = ClubMembershipStatus.Pending,
        IsAdmin = false,
        RequestedAt = DateTimeOffset.UtcNow,
    });
    await db.SaveChangesAsync();
    return Results.Ok();
})
.RequireAuthorization()
.WithName("JoinClub");

app.MapPost("/clubs/{clubId:guid}/leave", async (Guid clubId, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var membership = await db.ClubMemberships.FirstOrDefaultAsync(m => m.ClubId == clubId && m.UserId == userId);
    if (membership is null)
        return Results.NotFound();

    db.ClubMemberships.Remove(membership);
    await db.SaveChangesAsync();
    return Results.Ok();
})
.RequireAuthorization()
.WithName("LeaveClub");

app.MapGet("/clubs/{clubId:guid}/members/pending", async (Guid clubId, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    if (!await IsClubAdminAsync(db, clubId, userId))
        return Results.Forbid();

    var pending = await (
        from m in db.ClubMemberships
        join u in db.Users on m.UserId equals u.Id
        where m.ClubId == clubId && m.Status == ClubMembershipStatus.Pending
        select new PendingMemberDto(m.Id, u.Email!, m.RequestedAt))
        .ToListAsync();
    return Results.Ok(pending);
})
.RequireAuthorization()
.WithName("PendingClubMembers");

app.MapPost("/clubs/{clubId:guid}/members/{membershipId:guid}/approve", async (Guid clubId, Guid membershipId, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    if (!await IsClubAdminAsync(db, clubId, userId))
        return Results.Forbid();

    var membership = await db.ClubMemberships.FirstOrDefaultAsync(m => m.Id == membershipId && m.ClubId == clubId);
    if (membership is null)
        return Results.NotFound();

    membership.Status = ClubMembershipStatus.Approved;
    membership.DecidedAt = DateTimeOffset.UtcNow;
    membership.DecidedByUserId = userId;
    await db.SaveChangesAsync();
    return Results.Ok();
})
.RequireAuthorization()
.WithName("ApproveClubMember");

app.MapPost("/clubs/{clubId:guid}/members/{membershipId:guid}/reject", async (Guid clubId, Guid membershipId, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    if (!await IsClubAdminAsync(db, clubId, userId))
        return Results.Forbid();

    var membership = await db.ClubMemberships.FirstOrDefaultAsync(m => m.Id == membershipId && m.ClubId == clubId);
    if (membership is null)
        return Results.NotFound();

    db.ClubMemberships.Remove(membership);
    await db.SaveChangesAsync();
    return Results.Ok();
})
.RequireAuthorization()
.WithName("RejectClubMember");

// Persistierte Sperr-Bereiche (CONCEPT.md Phase-4-Backlog "Mehrbenutzerfähigkeit/Auth/Vereine",
// Stufe 3) - Ergaenzung zu den weiterhin rein Request-lokalen BlockedAreas (siehe /route unten,
// das beide zu einer Liste zusammenfuehrt).
app.MapGet("/segment-locks/mine", async (ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var locks = await db.SegmentLocks
        .Where(s => s.OwnerUserId == userId && s.ClubId == null)
        .Select(s => new SegmentLockDto(s.Id, s.Lat, s.Lon, s.RadiusMeters, s.Status.ToString(), s.CreatedAt))
        .ToListAsync();
    return Results.Ok(locks);
})
.RequireAuthorization()
.WithName("MySegmentLocks");

app.MapPost("/segment-locks/personal", async (SegmentLockRequest request, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    // Persoenliche Sperren brauchen keine Freigabe - sofort Active, siehe SegmentLock-Doc.
    var segmentLock = new SegmentLock
    {
        Id = Guid.NewGuid(),
        OwnerUserId = userId,
        ClubId = null,
        Lat = request.Lat,
        Lon = request.Lon,
        RadiusMeters = request.RadiusMeters,
        Status = SegmentLockStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
    };
    db.SegmentLocks.Add(segmentLock);
    await db.SaveChangesAsync();
    return Results.Ok(new SegmentLockDto(segmentLock.Id, segmentLock.Lat, segmentLock.Lon, segmentLock.RadiusMeters, segmentLock.Status.ToString(), segmentLock.CreatedAt));
})
.RequireAuthorization()
.WithName("CreatePersonalSegmentLock");

app.MapPost("/segment-locks/club", async (SegmentLockRequest request, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var membership = await db.ClubMemberships.FirstOrDefaultAsync(m => m.UserId == userId && m.Status == ClubMembershipStatus.Approved);
    if (membership is null)
        return Results.Forbid();

    // Vereins-Sperren starten Pending - erst nach Freigabe durch einen Verantwortlichen aktiv
    // (siehe /segment-locks/{id}/approve).
    var segmentLock = new SegmentLock
    {
        Id = Guid.NewGuid(),
        OwnerUserId = userId,
        ClubId = membership.ClubId,
        Lat = request.Lat,
        Lon = request.Lon,
        RadiusMeters = request.RadiusMeters,
        Status = SegmentLockStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
    };
    db.SegmentLocks.Add(segmentLock);
    await db.SaveChangesAsync();
    return Results.Ok(new SegmentLockDto(segmentLock.Id, segmentLock.Lat, segmentLock.Lon, segmentLock.RadiusMeters, segmentLock.Status.ToString(), segmentLock.CreatedAt));
})
.RequireAuthorization()
.WithName("ProposeClubSegmentLock");

app.MapDelete("/segment-locks/{id:guid}", async (Guid id, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var segmentLock = await db.SegmentLocks.FindAsync(id);
    if (segmentLock is null)
        return Results.NotFound();

    var isOwner = segmentLock.OwnerUserId == userId;
    var isClubAdmin = segmentLock.ClubId is Guid clubId && await IsClubAdminAsync(db, clubId, userId);
    if (!isOwner && !isClubAdmin)
        return Results.Forbid();

    db.SegmentLocks.Remove(segmentLock);
    await db.SaveChangesAsync();
    return Results.Ok();
})
.RequireAuthorization()
.WithName("DeleteSegmentLock");

app.MapGet("/clubs/{clubId:guid}/segment-locks/pending", async (Guid clubId, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    // Jedes Mitglied darf die Warteschlange EINSEHEN (Transparenz, auch fuer die eigenen
    // Vorschlaege) - nur das tatsaechliche Freigeben/Ablehnen bleibt Verantwortlichen
    // vorbehalten (siehe /segment-locks/{id}/approve|reject).
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var isMember = await db.ClubMemberships.AnyAsync(m => m.ClubId == clubId && m.UserId == userId && m.Status == ClubMembershipStatus.Approved);
    if (!isMember)
        return Results.Forbid();

    var pending = await db.SegmentLocks
        .Where(s => s.ClubId == clubId && s.Status == SegmentLockStatus.Pending)
        .Select(s => new SegmentLockDto(s.Id, s.Lat, s.Lon, s.RadiusMeters, s.Status.ToString(), s.CreatedAt))
        .ToListAsync();
    return Results.Ok(pending);
})
.RequireAuthorization()
.WithName("PendingClubSegmentLocks");

app.MapGet("/clubs/{clubId:guid}/segment-locks/active", async (Guid clubId, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var isMember = await db.ClubMemberships.AnyAsync(m => m.ClubId == clubId && m.UserId == userId && m.Status == ClubMembershipStatus.Approved);
    if (!isMember)
        return Results.Forbid();

    var active = await db.SegmentLocks
        .Where(s => s.ClubId == clubId && s.Status == SegmentLockStatus.Active)
        .Select(s => new SegmentLockDto(s.Id, s.Lat, s.Lon, s.RadiusMeters, s.Status.ToString(), s.CreatedAt))
        .ToListAsync();
    return Results.Ok(active);
})
.RequireAuthorization()
.WithName("ActiveClubSegmentLocks");

app.MapPost("/segment-locks/{id:guid}/approve", async (Guid id, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var segmentLock = await db.SegmentLocks.FindAsync(id);
    if (segmentLock?.ClubId is not Guid clubId)
        return Results.NotFound();
    if (!await IsClubAdminAsync(db, clubId, userId))
        return Results.Forbid();

    segmentLock.Status = SegmentLockStatus.Active;
    segmentLock.DecidedAt = DateTimeOffset.UtcNow;
    segmentLock.DecidedByUserId = userId;
    await db.SaveChangesAsync();
    return Results.Ok();
})
.RequireAuthorization()
.WithName("ApproveClubSegmentLock");

app.MapPost("/segment-locks/{id:guid}/reject", async (Guid id, ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var segmentLock = await db.SegmentLocks.FindAsync(id);
    if (segmentLock?.ClubId is not Guid clubId)
        return Results.NotFound();
    if (!await IsClubAdminAsync(db, clubId, userId))
        return Results.Forbid();

    segmentLock.Status = SegmentLockStatus.Rejected;
    segmentLock.DecidedAt = DateTimeOffset.UtcNow;
    segmentLock.DecidedByUserId = userId;
    await db.SaveChangesAsync();
    return Results.Ok();
})
.RequireAuthorization()
.WithName("RejectClubSegmentLock");

app.MapPost("/workout/build", (List<WorkoutBlockSpec> blocks) =>
{
    if (blocks.Count == 0)
        return Results.BadRequest("Mindestens ein Block wird benötigt.");

    try
    {
        var bytes = FitWorkoutEncoder.Encode(blocks);
        return Results.File(bytes, "application/octet-stream", "generated-workout.fit");
    }
    catch (NotSupportedException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
})
.RequireAuthorization()
.WithName("BuildWorkoutFit");

app.MapPost("/route", async (
    HttpRequest request, RouteConstructionService routeService, FitWorkoutParser fitParser, IConstructionClosureCache closureCache,
    ClaimsPrincipal principal, WattLoopDbContext db) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest("Erwartet wird multipart/form-data mit fitFile und Profil-Feldern.");

    var form = await request.ReadFormAsync();
    var fitFile = form.Files["fitFile"];
    if (fitFile is null)
        return Results.BadRequest("Feld 'fitFile' fehlt.");

    double ParseRequired(string key)
    {
        if (!double.TryParse(form[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"Feld '{key}' fehlt oder ist keine Zahl.");
        return value;
    }

    double ParseOptional(string key, double fallback)
    {
        return double.TryParse(form[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    double? ParseOptionalNullable(string key)
    {
        return double.TryParse(form[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    int? ParseOptionalNullableInt(string key)
    {
        return int.TryParse(form[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    // Erwartet einen ISO-8601-String MIT Zeitzonen-Offset (z.B. "...Z" fuer UTC) - das Frontend
    // wandelt den lokal im Browser gewaehlten Zeitpunkt (datetime-local-Input, kein eigenes
    // Zeitzonen-Wissen) selbst in UTC um, bevor er gesendet wird (siehe frontend/src/api.ts).
    // Ein Parse ohne Offset wuerde sonst stillschweigend die Serverzeitzone annehmen - auf
    // Render potenziell eine andere als die des Nutzers, ein reales Korrektheitsrisiko.
    DateTimeOffset? ParseOptionalDateTimeOffset(string key)
    {
        return DateTimeOffset.TryParse(form[key], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    }

    // Als JSON-Array im Formularfeld kodiert (nicht als einzelne Felder wie die uebrigen
    // Parameter), da es beliebig viele Sperrbereiche sein koennen - siehe frontend/src/api.ts.
    // PropertyNameCaseInsensitive noetig, da das Frontend camelCase ("lat"/"radiusMeters")
    // sendet, die BlockedAreaDto-Properties aber ueblicher C#-Konvention nach PascalCase sind.
    var blockedAreaJsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    List<BlockedArea> ParseBlockedAreas()
    {
        var raw = form["blockedAreas"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        try
        {
            var dtos = JsonSerializer.Deserialize<List<BlockedAreaDto>>(raw, blockedAreaJsonOptions)
                ?? throw new ArgumentException("Feld 'blockedAreas' ist kein gueltiges JSON-Array.");
            return dtos.Select(d => new BlockedArea(new GeoPoint(d.Lat, d.Lon), d.RadiusMeters)).ToList();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Feld 'blockedAreas' konnte nicht gelesen werden: {ex.Message}");
        }
    }

    // Analog zu ParseBlockedAreas - siehe RouteRequest.RequiredPoints (CONCEPT.md 6.19).
    List<GeoPoint> ParseRequiredPoints()
    {
        var raw = form["requiredPoints"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        try
        {
            var dtos = JsonSerializer.Deserialize<List<RequiredPointDto>>(raw, blockedAreaJsonOptions)
                ?? throw new ArgumentException("Feld 'requiredPoints' ist kein gueltiges JSON-Array.");
            return dtos.Select(d => new GeoPoint(d.Lat, d.Lon)).ToList();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Feld 'requiredPoints' konnte nicht gelesen werden: {ex.Message}");
        }
    }

    // Nur die IDs (nicht die vollen Baustellen-Daten - die liegen bereits serverseitig im Cache,
    // siehe /construction-closures), analog zu ParseBlockedAreas ein JSON-Array im Formularfeld.
    // Siehe CONCEPT.md Abschnitt 6.27: Nutzer kann eine automatisch erkannte Baustelle bewusst
    // fuer die eigene Route ignorieren, da die Daten editoriell kuratiert/nicht 100% verlaesslich
    // sind.
    List<string> ParseIgnoredConstructionClosureIds()
    {
        var raw = form["ignoredConstructionClosureIds"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw, blockedAreaJsonOptions)
                ?? throw new ArgumentException("Feld 'ignoredConstructionClosureIds' ist kein gueltiges JSON-Array.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Feld 'ignoredConstructionClosureIds' konnte nicht gelesen werden: {ex.Message}");
        }
    }

    RiderProfile rider;
    GeoPoint start;
    double maxApproachMinutes;
    SegmentReusePreference reuse;
    bool allowUTurns;
    double? maxUnpavedSegmentMeters;
    double? maxTotalUnpavedMeters;
    double? maxTotalRoughMeters;
    int? maxDisruptiveJunctions;
    int? maxRouteVariantAttempts;
    List<BlockedArea> blockedAreas;
    List<GeoPoint> requiredPoints;
    List<string> ignoredConstructionClosureIds;
    DateTimeOffset? plannedStartTime;
    bool showAlternatives;
    int? requestedSeed;
    try
    {
        rider = new RiderProfile
        {
            FtpWatts = ParseRequired("ftpWatts"),
            WeightKg = ParseRequired("weightKg"),
            SprintAvgWatts = ParseRequired("sprintAvgWatts"),
        };
        start = new GeoPoint(ParseRequired("startLat"), ParseRequired("startLon"));
        maxApproachMinutes = ParseOptional("maxApproachMinutes", 30);
        reuse = string.Equals(form["segmentReuse"], "PreferVariety", StringComparison.OrdinalIgnoreCase)
            ? SegmentReusePreference.PreferVariety
            : SegmentReusePreference.PreferReuse;
        allowUTurns = !string.Equals(form["allowUTurns"], "false", StringComparison.OrdinalIgnoreCase);
        maxUnpavedSegmentMeters = ParseOptionalNullable("maxUnpavedSegmentMeters");
        maxTotalUnpavedMeters = ParseOptionalNullable("maxTotalUnpavedMeters");
        maxTotalRoughMeters = ParseOptionalNullable("maxTotalRoughMeters");
        maxDisruptiveJunctions = ParseOptionalNullableInt("maxDisruptiveJunctions");
        maxRouteVariantAttempts = ParseOptionalNullableInt("maxRouteVariantAttempts");
        blockedAreas = ParseBlockedAreas();
        requiredPoints = ParseRequiredPoints();
        ignoredConstructionClosureIds = ParseIgnoredConstructionClosureIds();
        plannedStartTime = ParseOptionalDateTimeOffset("plannedStartTime");
        showAlternatives = string.Equals(form["showAlternatives"], "true", StringComparison.OrdinalIgnoreCase);
        requestedSeed = ParseOptionalNullableInt("seed");
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }

    TrainingPlan plan;
    try
    {
        await using var fitStream = fitFile.OpenReadStream();
        plan = fitParser.ParseWorkout(fitStream, rider);
    }
    catch (FitParsingException ex)
    {
        return Results.BadRequest($"FIT-Datei konnte nicht gelesen werden: {ex.Message}");
    }

    // Aktueller Cache-Stand, abzueglich der vom Nutzer fuer DIESE Route bewusst ignorierten
    // Baustellen (siehe ParseIgnoredConstructionClosureIds) - RouteConstructionService/
    // GraphHopperClient kennen weder den Cache noch die Ignorier-Liste, nur das fertig
    // gefilterte Ergebnis (siehe RouteRequest.ConstructionClosures).
    var activeConstructionClosures = closureCache.GetActive()
        .Where(c => !ignoredConstructionClosureIds.Contains(c.Id))
        .ToList();

    // Persistierte Sperr-Bereiche (Stufe 3, siehe /segment-locks/*) - eigene UND (falls
    // Mitglied) vom Verein freigegebene gelten automatisch bei JEDER Routenberechnung, genau wie
    // die vom Nutzer fuer DIESE eine Route gesetzten (rein Request-lokalen) blockedAreas oben.
    // GraphHopperClient.BuildCustomModel unterscheidet nicht zwischen den Quellen - eine
    // gesperrte Kreisflaeche bleibt eine gesperrte Kreisflaeche, unabhaengig davon WARUM.
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var myApprovedClubId = await db.ClubMemberships
        .Where(m => m.UserId == userId && m.Status == ClubMembershipStatus.Approved)
        .Select(m => (Guid?)m.ClubId)
        .FirstOrDefaultAsync();
    var persistedLocks = await db.SegmentLocks
        .Where(s => s.Status == SegmentLockStatus.Active
            && ((s.OwnerUserId == userId && s.ClubId == null) || (myApprovedClubId != null && s.ClubId == myApprovedClubId)))
        .ToListAsync();
    blockedAreas.AddRange(persistedLocks.Select(s => new BlockedArea(new GeoPoint(s.Lat, s.Lon), s.RadiusMeters)));

    var routeRequest = new RouteRequest
    {
        StartPoint = start,
        Plan = plan,
        Rider = rider,
        MaxApproachMinutes = maxApproachMinutes,
        SegmentReuse = reuse,
        AllowUTurns = allowUTurns,
        MaxUnpavedSegmentMeters = maxUnpavedSegmentMeters,
        MaxTotalUnpavedMeters = maxTotalUnpavedMeters,
        MaxTotalRoughMeters = maxTotalRoughMeters,
        MaxDisruptiveJunctions = maxDisruptiveJunctions,
        MaxRouteVariantAttempts = maxRouteVariantAttempts,
        BlockedAreas = blockedAreas,
        RequiredPoints = requiredPoints,
        ConstructionClosures = activeConstructionClosures,
        PlannedStartTime = plannedStartTime,
        ShowAlternatives = showAlternatives,
    };

    try
    {
        var isGpx = string.Equals(form["format"], "gpx", StringComparison.OrdinalIgnoreCase);
        // Ein mitgeschickter Seed (siehe RouteResult.Seed/frontend/src/App.tsx) reproduziert
        // beim GPX-Export deterministisch GENAU die angezeigte Variante, statt die Route (und
        // damit potenziell eine ANDERE Variante) fuer den Download frisch zu berechnen.
        var result = isGpx && requestedSeed is int seed
            ? await routeService.BuildRouteWithSeedAsync(routeRequest, seed)
            : await routeService.BuildRouteAsync(routeRequest);
        if (isGpx)
        {
            var gpx = GpxWriter.ToGpx(result);
            return Results.Text(gpx, "application/gpx+xml");
        }
        return Results.Ok(result);
    }
    catch (GraphHopperException ex)
    {
        return Results.Problem($"GraphHopper-Routing fehlgeschlagen: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
})
.DisableAntiforgery()
.RequireAuthorization()
.WithName("BuildRoute");

app.Run();

// Flaches JSON-Format fuer das "blockedAreas"-Formularfeld (siehe frontend/src/api.ts) - nicht
// direkt BlockedArea/GeoPoint, da deren Konstruktor-Parameternamen nicht 1:1 zu einer
// nutzerfreundlichen {lat, lon, radiusMeters}-Form passen.
internal sealed record BlockedAreaDto(double Lat, double Lon, double RadiusMeters);

// Analog zu BlockedAreaDto, siehe RouteRequest.RequiredPoints.
internal sealed record RequiredPointDto(double Lat, double Lon);

internal sealed record RegisterRequest(string Email, string Password);

internal sealed record LoginRequest(string Email, string Password);

internal sealed record AuthResponse(string Token, string Email);

// Siehe GET/PUT /profile - UserRiderProfile (Data) ohne UserId, das kommt aus dem JWT, nicht
// vom Client.
internal sealed record RiderProfileDto(double FtpWatts, double WeightKg, double SprintAvgWatts);

// Siehe POST/GET /clubs - Vereine (CONCEPT.md Phase-4-Backlog "Mehrbenutzerfähigkeit/Auth/
// Vereine", Stufe 2).
internal sealed record CreateClubRequest(string Name);
internal sealed record ClubDto(Guid Id, string Name, int MemberCount);
internal sealed record ClubMembershipDto(Guid ClubId, string ClubName, string Status, bool IsAdmin);
internal sealed record PendingMemberDto(Guid MembershipId, string Email, DateTimeOffset RequestedAt);

// Siehe POST /segment-locks/* - persistierte Sperr-Bereiche (Stufe 3), analog zu
// BlockedAreaDto, aber mit Id (fuer Loeschen/Freigeben) und Status statt rein Request-lokal.
internal sealed record SegmentLockRequest(double Lat, double Lon, double RadiusMeters);
internal sealed record SegmentLockDto(Guid Id, double Lat, double Lon, double RadiusMeters, string Status, DateTimeOffset CreatedAt);
