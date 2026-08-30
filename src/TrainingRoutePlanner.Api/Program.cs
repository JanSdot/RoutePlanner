using System.Globalization;
using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.FitParsing;
using TrainingRoutePlanner.OsmCorridors;
using TrainingRoutePlanner.PowerModel;
using TrainingRoutePlanner.RouteEngine;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

const string FrontendCorsPolicy = "FrontendDevServer";
builder.Services.AddCors(options =>
{
    // Phase 2 MVP: Vite-Dev-Server-Origin fest erlaubt, kein Auth/Multi-User noetig
    // (siehe CONCEPT.md Abschnitt 6 Phase 2).
    options.AddPolicy(FrontendCorsPolicy, policy =>
        policy.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddHttpClient<IGraphHopperClient, GraphHopperClient>((sp, http) =>
{
    var baseUrl = sp.GetRequiredService<IConfiguration>()["GraphHopper:BaseUrl"]
        ?? throw new InvalidOperationException("GraphHopper:BaseUrl fehlt in der Konfiguration.");
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

app.UseHttpsRedirection();
app.UseCors(FrontendCorsPolicy);

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
