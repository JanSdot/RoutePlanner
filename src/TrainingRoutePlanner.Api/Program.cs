using System.Globalization;
using System.Text.Json.Serialization;
using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.FitParsing;
using TrainingRoutePlanner.OsmCorridors;
using TrainingRoutePlanner.PowerModel;
using TrainingRoutePlanner.RouteEngine;

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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok());

// Render terminiert TLS an seinem eigenen Edge und leitet intern per HTTP weiter - ein
// erzwungenes Redirect hier wuerde ohne Forwarded-Header-Auswertung ins Leere laufen.
// Lokal (Development) bleibt es wie gehabt aktiv.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseCors(FrontendCorsPolicy);

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

    RiderProfile rider;
    GeoPoint start;
    double maxApproachMinutes;
    SegmentReusePreference reuse;
    bool allowUTurns;
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
