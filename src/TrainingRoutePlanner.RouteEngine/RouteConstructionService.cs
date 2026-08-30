using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.PowerModel;

namespace TrainingRoutePlanner.RouteEngine;

/// <summary>Setzt den Kernalgorithmus aus CONCEPT.md Abschnitt 4.2 (Korridor-Splicing), 4.3
/// (Fallback-Eskalation) und 4.4 (Anfahrt-Budget) sowie die hoehenprofil-iterative
/// Distanzverfeinerung aus 3.3 um. Verbleibende bewusste Vereinfachung (siehe Abschnitt 7):
/// Korridorsuche ist ein linearer Scan ohne Spatial Index.</summary>
public sealed class RouteConstructionService(
    IGraphHopperClient graphHopper,
    ICorridorIndex corridorIndex,
    PowerSpeedModel powerModel)
{
    private const int RoundTripSeed = 1;
    private const double InitialSearchRadiusMeters = 800;
    private const int MaxFallbackAttempts = 4;
    private const double SearchRadiusGrowthFactor = 2.0;
    private const double ScoreRelaxationFactor = 1.5;

    // Trennt "ruhige" Bloecke (GA1/GA2, hohe Toleranz) von "Effort"-Bloecken, die einen
    // dedizierten Korridor brauchen (EB/SB/VO2max/Sprint) - siehe ZoneBands in Domain.
    private const double DedicatedCorridorScoreCutoff = 5.0;

    // Hoehenprofil-iterative Distanzverfeinerung, siehe CONCEPT.md 3.3: mehr Iterationen
    // bringen abnehmenden Ertrag gegen zusaetzliche GraphHopper-Anfragen, daher gedeckelt.
    private const int MaxDistanceRefinementIterations = 3;
    private const double DistanceRefinementToleranceFraction = 0.05;
    private const double GradientSampleWindowMeters = 400;

    public async Task<RouteResult> BuildRouteAsync(RouteRequest request, CancellationToken ct = default)
    {
        var warnings = new List<RouteWarning>();
        var maxApproachRadiusMeters = ComputeMaxApproachRadiusMeters(request);

        var (roughLoop, stepDistances) = await RefineRoughLoopAsync(request, ct);
        var roughLoopLength = PolylineMath.TotalLengthMeters(roughLoop.Geometry);
        var totalDistance = stepDistances.Sum();

        var waypoints = new List<GeoPoint> { request.StartPoint };
        var reuseCache = new Dictionary<(double PowerBucket, double ScoreBucket), Corridor>();
        var cumulativeDistance = 0.0;

        for (var i = 0; i < request.Plan.Steps.Count; i++)
        {
            var step = request.Plan.Steps[i];
            var stepDistance = stepDistances[i];
            var stepStartFraction = totalDistance <= 0 ? 0 : cumulativeDistance / totalDistance;
            cumulativeDistance += stepDistance;

            if (step.MaxDisruptionScore > DedicatedCorridorScoreCutoff)
                continue; // ruhiger Block, laeuft ueber die normale Verbindungsstrecke

            var targetPoint = PolylineMath.PointAtDistance(roughLoop.Geometry, stepStartFraction * roughLoopLength);
            var corridor = FindCorridorWithFallback(
                step, stepDistance, targetPoint, request.SegmentReuse, reuseCache, maxApproachRadiusMeters, warnings);
            if (corridor is not null)
            {
                waypoints.Add(corridor.Start);
                waypoints.Add(corridor.End);
            }
        }
        waypoints.Add(request.StartPoint);

        var finalRoute = waypoints.Count > 2
            ? await graphHopper.RouteThroughWaypointsAsync(waypoints, ct)
            : roughLoop;

        CheckApproachBudget(request, finalRoute, warnings);

        return new RouteResult
        {
            Geometry = finalRoute.Geometry,
            TotalDistanceMeters = finalRoute.DistanceMeters,
            EstimatedTotalTime = finalRoute.Time,
            Warnings = warnings,
        };
    }

    /// <summary>Fragt round_trip an, verfeinert die pro-Schritt-Distanzen anhand des
    /// tatsaechlichen Hoehenprofils der Antwort (statt der reinen Flach-Annahme aus 3.3) und
    /// fragt bei signifikanter Gesamtabweichung erneut an - der "iterative Prozess" aus 3.3.
    /// Die Position jedes Schritts entlang der Schleife wird pro Iteration anhand der
    /// Distanzen der VORHERIGEN Iteration bestimmt (nicht der gerade neu berechneten) - sonst
    /// haengt die Positionsbestimmung von einem Wert ab, den sie selbst gerade erst liefert.</summary>
    private async Task<(GraphHopperRoute RoughLoop, double[] StepDistances)> RefineRoughLoopAsync(RouteRequest request, CancellationToken ct)
    {
        var steps = request.Plan.Steps;
        var stepDistances = steps.Select(s => EstimateDistance(s, request.Rider, gradient: 0.0)).ToArray();
        var totalDistance = stepDistances.Sum();

        var roughLoop = await graphHopper.RoundTripAsync(request.StartPoint, totalDistance, RoundTripSeed, ct);

        for (var iteration = 0; iteration < MaxDistanceRefinementIterations; iteration++)
        {
            var roughLoopLength = PolylineMath.TotalLengthMeters(roughLoop.Geometry);
            var refined = new double[steps.Count];
            var cumulative = 0.0;

            for (var i = 0; i < steps.Count; i++)
            {
                var fraction = totalDistance <= 0 ? 0 : cumulative / totalDistance;
                var gradient = PolylineMath.AverageGradient(roughLoop.Geometry, fraction * roughLoopLength, GradientSampleWindowMeters);
                refined[i] = EstimateDistance(steps[i], request.Rider, gradient);
                cumulative += stepDistances[i];
            }

            var refinedTotal = refined.Sum();
            var relativeChange = totalDistance <= 0 ? 0 : Math.Abs(refinedTotal - totalDistance) / totalDistance;

            stepDistances = refined;
            totalDistance = refinedTotal;

            if (relativeChange < DistanceRefinementToleranceFraction)
                break;

            roughLoop = await graphHopper.RoundTripAsync(request.StartPoint, totalDistance, RoundTripSeed, ct);
        }

        return (roughLoop, stepDistances);
    }

    private double EstimateDistance(TrainingStep step, RiderProfile profile, double gradient) =>
        powerModel.SolveSpeedMps(step.TargetPowerWatts, profile, gradient) * step.Duration.TotalSeconds;

    /// <summary>Wandelt CONCEPT.md 4.4's Anfahrt-Zeitbudget in einen Distanz-Deckel fuer die
    /// Korridorsuche um, angenommen bei GA1-Tempo (dem ruhigsten Zonentyp - die Anfahrt selbst
    /// ist per Definition ein ruhiger Blockanteil). Budget gilt fuer Hin- UND Rueckweg
    /// zusammen, daher Division durch 2 fuer eine Richtung.</summary>
    private double ComputeMaxApproachRadiusMeters(RouteRequest request)
    {
        var quietStep = ZoneResolver.FromZone(TrainingZone.GA1, TimeSpan.FromMinutes(1), request.Rider);
        var quietSpeedMps = powerModel.SolveSpeedMps(quietStep.TargetPowerWatts, request.Rider);
        return quietSpeedMps * (request.MaxApproachMinutes * 60.0 / 2.0);
    }

    private Corridor? FindCorridorWithFallback(
        TrainingStep step,
        double requiredLengthMeters,
        GeoPoint targetPoint,
        SegmentReusePreference reusePreference,
        Dictionary<(double, double), Corridor> reuseCache,
        double maxSearchRadiusMeters,
        List<RouteWarning> warnings)
    {
        var cacheKey = (Math.Round(step.TargetPowerWatts / 10) * 10, step.MaxDisruptionScore);
        if (reusePreference == SegmentReusePreference.PreferReuse && reuseCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var searchRadius = Math.Min(InitialSearchRadiusMeters, maxSearchRadiusMeters);
        var maxScore = step.MaxDisruptionScore;

        // Eskalationskette aus CONCEPT.md 4.3: (1) strikt, (2) automatisch lockern - aber nie
        // ueber das Anfahrt-Budget aus 4.4 hinaus (maxSearchRadiusMeters).
        for (var attempt = 0; attempt < MaxFallbackAttempts; attempt++)
        {
            var found = corridorIndex.TryFindCorridor(targetPoint, requiredLengthMeters, maxScore, searchRadius);
            if (found is not null)
            {
                if (attempt > 0)
                {
                    warnings.Add(new RouteWarning
                    {
                        Message = $"Für '{step.Label ?? "Trainingsschritt"}' wurde kein optimaler Korridor gefunden - " +
                                  "Suchradius/Score-Schwelle wurden automatisch gelockert.",
                        Location = targetPoint,
                    });
                }
                return CacheIfPreferred(found, cacheKey, reusePreference, reuseCache);
            }
            if (searchRadius >= maxSearchRadiusMeters)
                break;
            searchRadius = Math.Min(searchRadius * SearchRadiusGrowthFactor, maxSearchRadiusMeters);
            maxScore *= ScoreRelaxationFactor;
        }

        // (3) Bestmoeglichen Kandidaten nehmen: Laengenanforderung fallen lassen, Score-
        // Schwelle grosszuegig oeffnen - lieber ein kompromittierter Korridor als gar keiner.
        var bestEffort = corridorIndex.TryFindCorridor(targetPoint, minLengthMeters: 200, maxDisruptionScore: 100, searchRadius);
        if (bestEffort is not null)
        {
            warnings.Add(new RouteWarning
            {
                Message = $"Für '{step.Label ?? "Trainingsschritt"}' konnte kein Korridor gefunden werden, der Länge " +
                          $"({requiredLengthMeters:F0} m) und Unterbrechungsschwelle ({step.MaxDisruptionScore:F1}) erfüllt - " +
                          "es wurde der bestmögliche verfügbare Korridor als Kompromiss verwendet.",
                Location = targetPoint,
            });
            return CacheIfPreferred(bestEffort, cacheKey, reusePreference, reuseCache);
        }

        warnings.Add(new RouteWarning
        {
            Message = $"Für '{step.Label ?? "Trainingsschritt"}' konnte kein Korridor gefunden werden - " +
                      "dieser Abschnitt läuft über die normale Streckenführung ohne Sonderbehandlung.",
            Location = targetPoint,
        });
        return null;
    }

    private static Corridor CacheIfPreferred(
        Corridor corridor, (double, double) cacheKey, SegmentReusePreference preference,
        Dictionary<(double, double), Corridor> cache)
    {
        if (preference == SegmentReusePreference.PreferReuse)
            cache[cacheKey] = corridor;
        return corridor;
    }

    private static void CheckApproachBudget(RouteRequest request, GraphHopperRoute finalRoute, List<RouteWarning> warnings)
    {
        var prescribedTicks = request.Plan.Steps.Sum(s => s.Duration.Ticks);
        var prescribedTime = TimeSpan.FromTicks(prescribedTicks);
        var extraTime = finalRoute.Time - prescribedTime;
        if (extraTime.TotalMinutes > request.MaxApproachMinutes)
        {
            warnings.Add(new RouteWarning
            {
                Message = $"Route ist {extraTime.TotalMinutes:F0} min länger als der reine Trainingsplan " +
                          $"(Budget: {request.MaxApproachMinutes:F0} min) - die nächste geeignete Strecke liegt weiter entfernt.",
            });
        }
    }
}
