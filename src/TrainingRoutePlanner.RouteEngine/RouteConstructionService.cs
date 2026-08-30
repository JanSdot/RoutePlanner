using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.PowerModel;

namespace TrainingRoutePlanner.RouteEngine;

/// <summary>Setzt den Kernalgorithmus aus CONCEPT.md Abschnitt 4.2 (Korridor-Splicing) und
/// 4.3 (Fallback-Eskalation) um. Bewusste Vereinfachungen fuer Phase 1 (siehe auch CONCEPT.md
/// Abschnitt 7): Distanzschaetzung nutzt nur die Flach-Annahme aus 3.3, die hoehenprofil-
/// iterative Verfeinerung (Bisektion auf die angefragte round_trip-Distanz) ist noch nicht
/// implementiert. Die Anfahrt-Budget-Absorption aus 4.4 (ruhige Bloecke als Anfahrt-Budget
/// nutzen) ist ebenfalls noch nicht aktiv - Anfahrt wird hier nur als Warnung sichtbar, nicht
/// aktiv in die Routenplanung eingerechnet.</summary>
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

    public async Task<RouteResult> BuildRouteAsync(RouteRequest request, CancellationToken ct = default)
    {
        var warnings = new List<RouteWarning>();

        var stepEstimates = request.Plan.Steps
            .Select(step => new StepEstimate(step, EstimateFlatDistance(step, request.Rider)))
            .ToList();
        var totalFlatDistance = stepEstimates.Sum(s => s.FlatDistanceMeters);

        var roughLoop = await graphHopper.RoundTripAsync(request.StartPoint, totalFlatDistance, RoundTripSeed, ct);
        var roughLoopLength = PolylineMath.TotalLengthMeters(roughLoop.Geometry);

        var waypoints = new List<GeoPoint> { request.StartPoint };
        var reuseCache = new Dictionary<(double PowerBucket, double ScoreBucket), Corridor>();
        var cumulativeDistance = 0.0;

        foreach (var estimate in stepEstimates)
        {
            var step = estimate.Step;
            var stepStartFraction = totalFlatDistance <= 0 ? 0 : cumulativeDistance / totalFlatDistance;
            cumulativeDistance += estimate.FlatDistanceMeters;

            if (step.MaxDisruptionScore > DedicatedCorridorScoreCutoff)
                continue; // ruhiger Block, laeuft ueber die normale Verbindungsstrecke

            var targetPoint = PolylineMath.PointAtDistance(roughLoop.Geometry, stepStartFraction * roughLoopLength);
            var corridor = FindCorridorWithFallback(step, estimate.FlatDistanceMeters, targetPoint, request.SegmentReuse, reuseCache, warnings);
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

    private double EstimateFlatDistance(TrainingStep step, RiderProfile profile)
    {
        var speedMps = powerModel.SolveSpeedMps(step.TargetPowerWatts, profile);
        return speedMps * step.Duration.TotalSeconds;
    }

    private Corridor? FindCorridorWithFallback(
        TrainingStep step,
        double requiredLengthMeters,
        GeoPoint targetPoint,
        SegmentReusePreference reusePreference,
        Dictionary<(double, double), Corridor> reuseCache,
        List<RouteWarning> warnings)
    {
        var cacheKey = (Math.Round(step.TargetPowerWatts / 10) * 10, step.MaxDisruptionScore);
        if (reusePreference == SegmentReusePreference.PreferReuse && reuseCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var searchRadius = InitialSearchRadiusMeters;
        var maxScore = step.MaxDisruptionScore;

        // Eskalationskette aus CONCEPT.md 4.3: (1) strikt, (2) automatisch lockern.
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
            searchRadius *= SearchRadiusGrowthFactor;
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

    private sealed record StepEstimate(TrainingStep Step, double FlatDistanceMeters);
}
