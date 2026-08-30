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
    .WithName("GetJunctions");

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
.WithName("BuildWorkoutFit");

app.MapPost("/route", async (HttpRequest request, RouteConstructionService routeService, FitWorkoutParser fitParser) =>
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

    RiderProfile rider;
    GeoPoint start;
    double maxApproachMinutes;
    SegmentReusePreference reuse;
    bool allowUTurns;
    double? maxUnpavedSegmentMeters;
    double? maxTotalUnpavedMeters;
    int? maxDisruptiveJunctions;
    int? maxRouteVariantAttempts;
    List<BlockedArea> blockedAreas;
    List<GeoPoint> requiredPoints;
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
        maxDisruptiveJunctions = ParseOptionalNullableInt("maxDisruptiveJunctions");
        maxRouteVariantAttempts = ParseOptionalNullableInt("maxRouteVariantAttempts");
        blockedAreas = ParseBlockedAreas();
        requiredPoints = ParseRequiredPoints();
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
        MaxDisruptiveJunctions = maxDisruptiveJunctions,
        MaxRouteVariantAttempts = maxRouteVariantAttempts,
        BlockedAreas = blockedAreas,
        RequiredPoints = requiredPoints,
    };

    try
    {
        var result = await routeService.BuildRouteAsync(routeRequest);
        if (string.Equals(form["format"], "gpx", StringComparison.OrdinalIgnoreCase))
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
