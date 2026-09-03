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
    PowerSpeedModel powerModel,
    IWindForecastClient windForecastClient)
{
    // Fenstergroesse fuer die Peilungs-Stichprobe pro Trainingsschritt (AverageBearingDegrees) -
    // groesser als bei der Steigung (GradientSampleWindowMeters), da Fahrtrichtung sich ueber
    // kuerzere Distanzen staerker verrauscht (einzelne Kurven) als das Hoehenprofil.
    private const double BearingSampleWindowMeters = 800;
    private const int RoundTripSeedBase = 1;
    private const double InitialSearchRadiusMeters = 800;
    private const int MaxFallbackAttempts = 4;
    private const double SearchRadiusGrowthFactor = 2.0;
    private const double ScoreRelaxationFactor = 1.5;

    // Nur relevant, wenn RouteRequest.MaxUnpavedSegmentMeters/MaxTotalUnpavedMeters/
    // MaxDisruptiveJunctions gesetzt sind - jeder Versuch nutzt einen anderen round_trip-Seed
    // und damit eine andere Streckenfuehrung. Bricht beim ERSTEN Versuch ab, der ALLE gesetzten
    // Grenzwerte einhaelt (schnell im Normalfall - das GraphHopper-Profil bewertet unbefestigte
    // Oberflaechen seit 6.12 selbst ab, ein einzelner Versuch trifft die Grenzwerte daher
    // meistens schon). Ohne Erfolg wird der Versuch mit der geringsten "Badness" (siehe
    // RouteVariantBadness) zurueckgegeben, mit Warnung statt Garantie.
    // MaxSurfaceAvoidanceTimeBudget ist das Sicherheitsnetz gegen genau die Situation, die live
    // einmal zu einem kompletten Timeout gefuehrt hat (Render, unrealistisch enge Grenzwerte,
    // siehe CONCEPT.md 6.12): ein einzelner Versuch dauert dort im warmen Zustand ~15s (lokal
    // ~3s) - ohne Zeitbudget wuerden bei unerfuellbaren Limits IMMER alle Versuche durchlaufen,
    // weit ueber jedes sinnvolle Anfrage-Timeout hinaus. Standardwert, per
    // RouteRequest.MaxRouteVariantAttempts pro Anfrage ueberschreibbar (siehe CONCEPT.md 6.15) -
    // das Zeitbudget bleibt davon unabhaengig bestehen, auch bei einem hoch gesetzten Wert.
    private const int MaxSurfaceAvoidanceAttempts = 10;
    private static readonly TimeSpan MaxSurfaceAvoidanceTimeBudget = TimeSpan.FromSeconds(45);

    // Ein Punkt der Routengeometrie gilt als "an" einer Ampel/Stopp-Kreuzung, wenn er innerhalb
    // dieses Radius liegt - siehe CorridorIndex.CountDisruptiveJunctionsNear.
    private const double JunctionProximityMeters = 25.0;

    // Abstand zwischen zusaetzlichen "Anker"-Wegpunkten, die zwischen den echten Wegpunkten
    // (Korridor-Start/Ende, Pflicht-Wegpunkte) aus der roughLoop-Geometrie eingestreut werden -
    // siehe AddRoughLoopAnchors. Groessenordnung wie InitialSearchRadiusMeters, aber fuer einen
    // anderen Zweck (Distanztreue statt Korridorsuche): eng genug, damit GraphHoppers
    // Punkt-zu-Punkt-Routing der bekannten Schleifenform folgt statt abzukuerzen, aber nicht so
    // eng, dass die Wegpunkt-Liste bei langen Routen unnoetig aufgeblaeht wird.
    private const double RoughLoopAnchorSpacingMeters = 2000.0;

    // Heuristische Gewichtung fuer den Fallback-Vergleich, WENN kein Versuch alle Grenzwerte
    // einhaelt: "eine Kreuzung vermeiden ist ungefaehr so viel wert wie 300m unbefestigten
    // Untergrund vermeiden". Bewusst grob - beeinflusst nur die Auswahl des bestmoeglichen
    // Kompromisses, nicht ob ein Versuch die tatsaechlichen Nutzer-Grenzwerte erfuellt.
    private const double JunctionBadnessWeightMeters = 300.0;

    // Bewertet, wie weit ein Versuch in der GESAMTDAUER vom Trainingsplan abweicht (siehe
    // CheckDurationMismatch) - fehlte urspruenglich komplett in der Badness-Bewertung, wodurch
    // ein round_trip-Seed, der zufaellig eine deutlich KUERZERE Schleife lieferte, automatisch
    // "besser" abschnitt (weniger absolute unbefestigte Meter/Kreuzungen einer kuerzeren Route),
    // obwohl er den eigentlichen Trainingsplan (z.B. 120 min GA1) komplett verfehlt - live
    // beobachtet: 24 km/58 min statt der angeforderten ~120 min. 10 "Badness-Meter" pro Sekunde
    // Abweichung sorgt dafuer, dass schon wenige Minuten Abweichung jede realistische
    // Untergrund-/Kreuzungs-Differenz dominieren (60s*10=600 vs. eine einzelne Kreuzung=300),
    // waehrend Abweichungen im Sekundenbereich (round_trip trifft die Zieldistanz ohnehin nie
    // exakt) die Auswahl nicht unnoetig verzerren.
    private const double DurationMismatchBadnessWeightMetersPerSecond = 10.0;

    // "Gut genug" bei AUSSCHLIESSLICH Pflicht-Wegpunkten (kein explizites Untergrund-/
    // Kreuzungs-Limit) - eine Streckenvariante muss die Trainingsdauer nicht exakt treffen, um
    // sofort verwendet zu werden, nur nah genug (siehe hasExplicitLimit in BuildRouteAsync und
    // CONCEPT.md Bugfix-Abschnitt zum Pflicht-Wegpunkt-Umweg-Bug).
    private static readonly TimeSpan RequiredPointGoodEnoughDurationMismatch = TimeSpan.FromMinutes(2);
    private static readonly double RequiredPointGoodEnoughDurationMismatchSeconds = RequiredPointGoodEnoughDurationMismatch.TotalSeconds;

    // Trennt "ruhige" Bloecke (GA1/GA2, hohe Toleranz) von "Effort"-Bloecken, die einen
    // dedizierten Korridor brauchen (EB/SB/VO2max/Sprint) - siehe ZoneBands in Domain.
    private const double DedicatedCorridorScoreCutoff = 5.0;

    // Hoehenprofil-iterative Distanzverfeinerung, siehe CONCEPT.md 3.3: mehr Iterationen
    // bringen abnehmenden Ertrag gegen zusaetzliche GraphHopper-Anfragen, daher gedeckelt.
    private const int MaxDistanceRefinementIterations = 3;
    private const double DistanceRefinementToleranceFraction = 0.05;
    private const double GradientSampleWindowMeters = 400;

    // Nur relevant, wenn RouteRequest.ShowAlternatives gesetzt ist (siehe
    // BuildRouteWithAlternativesAsync). Grosszuegiger als MaxSurfaceAvoidanceTimeBudget, da hier
    // bewusst MEHRERE gute Kandidaten gesucht werden statt nur eines ausreichenden - der Nutzer
    // hat dem laenger dauernden Suchlauf ueber die Checkbox aktiv zugestimmt.
    private const int MaxAlternativeSeedAttempts = 8;
    private static readonly TimeSpan AlternativeTimeBudget = TimeSpan.FromSeconds(60);
    private const int AlternativeTargetCount = 3;

    // Ein Kandidat gilt nur dann als "eigene" Alternative (statt im Wesentlichen derselben
    // Schleife mit einer anderen Randnotiz), wenn ein spuerbarer Anteil seiner Laenge deutlich
    // (mehr als AlternativeDivergenceThresholdMeters) von JEDER bereits akzeptierten Variante
    // abweicht - siehe IsSufficientlyDifferent.
    private const int AlternativeGeometrySampleCount = 40;
    private const double AlternativeDivergenceThresholdMeters = 150.0;
    private const double MinAlternativeDivergenceFraction = 0.3;

    public async Task<RouteResult> BuildRouteAsync(RouteRequest request, CancellationToken ct = default)
    {
        var wind = await GetWindAsync(request, ct);

        if (request.ShowAlternatives)
            return await BuildRouteWithAlternativesAsync(request, wind, ct);

        // Ob ueberhaupt ein explizites Limit gesetzt ist (Untergrund/Kreuzungen). Pflicht-
        // Wegpunkte lösen den Retry-Mechanismus ZUSAETZLICH aus, auch ohne jedes Limit: ein
        // einzelner, vom natuerlichen Schleifenverlauf weit entfernter Pflicht-Wegpunkt kann je
        // nach round_trip-Seed einen unterschiedlich grossen Umweg erzwingen (siehe
        // AddRoughLoopAnchors) - mehrere Seeds durchzuprobieren und den mit der geringsten
        // Distanzabweichung zu waehlen, statt den ersten beliebigen Seed zu nehmen, behebt den
        // live gemeldeten Bug "es wird extra zum Punkt geroutet statt ihn zu integrieren, die
        // Route ist laenger als gewuenscht" (siehe CONCEPT.md Bugfix-Abschnitt).
        var hasExplicitLimit = request.MaxUnpavedSegmentMeters is not null || request.MaxTotalUnpavedMeters is not null
            || request.MaxTotalRoughMeters is not null || request.MaxDisruptiveJunctions is not null;
        if (!hasExplicitLimit && request.RequiredPoints.Count == 0)
            return await BuildRouteAttemptAsync(request, RoundTripSeedBase, wind, ct);

        // Mindestens 1, sonst wuerde eine versehentliche 0/negative Nutzereingabe die Schleife
        // nie durchlaufen und bestResult bliebe null.
        var maxAttempts = Math.Max(1, request.MaxRouteVariantAttempts ?? MaxSurfaceAvoidanceAttempts);
        var prescribedTime = TimeSpan.FromTicks(request.Plan.Steps.Sum(s => s.Duration.Ticks));

        RouteResult? bestResult = null;
        var bestBadness = double.MaxValue;
        var bestUnpavedTotalMeters = 0.0;
        var bestRoughTotalMeters = 0.0;
        var bestJunctionCount = 0;
        var attemptsMade = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            attemptsMade++;
            var result = await BuildRouteAttemptAsync(request, RoundTripSeedBase + attempt, wind, ct);
            var score = ScoreAttempt(request, result, prescribedTime);
            var (badness, unpavedTotalMeters, maxUnpavedSegmentMeters, roughTotalMeters, junctionCount, durationMismatchSeconds) = score;
            if (badness < bestBadness)
            {
                bestBadness = badness;
                bestResult = result;
                bestUnpavedTotalMeters = unpavedTotalMeters;
                bestRoughTotalMeters = roughTotalMeters;
                bestJunctionCount = junctionCount;
            }

            var withinSegmentLimit = request.MaxUnpavedSegmentMeters is not double segLimit || maxUnpavedSegmentMeters <= segLimit;
            var withinTotalLimit = request.MaxTotalUnpavedMeters is not double totalLimit || unpavedTotalMeters <= totalLimit;
            var withinRoughTotalLimit = request.MaxTotalRoughMeters is not double roughLimit || roughTotalMeters <= roughLimit;
            var withinJunctionLimit = request.MaxDisruptiveJunctions is not int juncLimit || junctionCount <= juncLimit;
            // Ohne jedes explizite Limit (nur Pflicht-Wegpunkte ausgeloest) gibt es kein "haelt
            // Grenzwerte ein" zu pruefen - stattdessen genuegt eine Variante, deren Gesamtdauer
            // schon nah genug am Trainingsplan liegt, statt zwingend alle maxAttempts Seeds
            // durchzuprobieren.
            var isGoodEnough = hasExplicitLimit
                ? withinSegmentLimit && withinTotalLimit && withinRoughTotalLimit && withinJunctionLimit
                : durationMismatchSeconds <= RequiredPointGoodEnoughDurationMismatchSeconds;
            if (isGoodEnough)
                return result;

            if (stopwatch.Elapsed >= MaxSurfaceAvoidanceTimeBudget)
                break;
        }

        var warnings = bestResult!.Warnings.ToList();
        warnings.Add(new RouteWarning
        {
            Message = hasExplicitLimit
                ? $"Keine der {attemptsMade} probierten Streckenvarianten hielt die gesetzten " +
                  "Grenzwerte (Untergrund/Kreuzungen) ein - die beste gefundene Variante " +
                  $"({bestUnpavedTotalMeters:F0} m unbefestigt, {bestRoughTotalMeters:F0} m rauer Belag, " +
                  $"{bestJunctionCount} Ampel-/Stopp-Kreuzungen) wurde stattdessen verwendet."
                : $"Der Pflicht-Wegpunkt liegt weit vom natürlichen Streckenverlauf entfernt - " +
                  $"keine der {attemptsMade} probierten Streckenvarianten kam nah an die geplante " +
                  "Trainingsdauer heran, die beste gefundene Variante wurde stattdessen verwendet.",
        });
        return new RouteResult
        {
            Geometry = bestResult.Geometry,
            TotalDistanceMeters = bestResult.TotalDistanceMeters,
            EstimatedTotalTime = bestResult.EstimatedTotalTime,
            Warnings = warnings,
            Segments = bestResult.Segments,
            SurfaceSegments = bestResult.SurfaceSegments,
            SmoothnessSegments = bestResult.SmoothnessSegments,
            Wind = bestResult.Wind,
            Seed = bestResult.Seed,
            Alternatives = bestResult.Alternatives,
        };
    }

    /// <summary>Holt die Windvorhersage einmalig fuer die gesamte Anfrage (siehe CONCEPT.md
    /// Phase-4-Backlog "Windmodellierung") - Ort und Zeitpunkt aendern sich zwischen Versuchen
    /// nie, ein wiederholter Abruf waere reine Verschwendung. Liefert null sowohl ohne gesetzten
    /// PlannedStartTime als auch bei einem Vorhersage-Fehler - beides bedeutet schlicht "keine
    /// Windkomponente in der Zeitschaetzung", kein Abbruch.</summary>
    private async Task<WindConditions?> GetWindAsync(RouteRequest request, CancellationToken ct)
    {
        return request.PlannedStartTime is DateTimeOffset plannedStartTime
            ? await windForecastClient.GetForecastAsync(request.StartPoint, plannedStartTime, ct)
            : null;
    }

    /// <summary>Deterministischer Direktzugriff auf einen bestimmten round_trip-Seed, OHNE jede
    /// Retry-/Badness-Logik - genutzt vom GPX-Export (Program.cs), um exakt die Geometrie zu
    /// reproduzieren, die dem Nutzer gerade angezeigt wird (RouteResult.Seed), statt bei jedem
    /// Download eine potenziell abweichende Variante frisch zu berechnen.</summary>
    public async Task<RouteResult> BuildRouteWithSeedAsync(RouteRequest request, int seed, CancellationToken ct = default)
    {
        var wind = await GetWindAsync(request, ct);
        return await BuildRouteAttemptAsync(request, seed, wind, ct);
    }

    /// <summary>Sucht bis zu AlternativeTargetCount hinreichend unterschiedliche Streckenvarianten
    /// (siehe RouteRequest.ShowAlternatives, IsSufficientlyDifferent) statt wie die normale
    /// Retry-Schleife nur EINE gute genug zu behalten. Der erste Versuch wird immer akzeptiert
    /// (nichts zum Vergleichen); jeder weitere nur, wenn er von ALLEN bereits akzeptierten
    /// Kandidaten spuerbar abweicht. Bricht ab, sobald genug Kandidaten gefunden sind, das
    /// Zeitbudget ueberschritten ist, oder die Seeds ausgehen. Der Kandidat mit der geringsten
    /// Badness wird zur primaeren Antwort, die uebrigen wandern in Alternatives.</summary>
    private async Task<RouteResult> BuildRouteWithAlternativesAsync(RouteRequest request, WindConditions? wind, CancellationToken ct)
    {
        var prescribedTime = TimeSpan.FromTicks(request.Plan.Steps.Sum(s => s.Duration.Ticks));
        var accepted = new List<(RouteResult Result, double Badness)>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (var attempt = 0; attempt < MaxAlternativeSeedAttempts && accepted.Count < AlternativeTargetCount; attempt++)
        {
            var result = await BuildRouteAttemptAsync(request, RoundTripSeedBase + attempt, wind, ct);
            var score = ScoreAttempt(request, result, prescribedTime);
            var sufficientlyDifferent = accepted.All(a => IsSufficientlyDifferent(result.Geometry, a.Result.Geometry));
            if (sufficientlyDifferent)
                accepted.Add((result, score.Badness));

            if (stopwatch.Elapsed >= AlternativeTimeBudget)
                break;
        }

        // accepted enthaelt immer mindestens den ersten Versuch (nichts zum Vergleichen bei
        // leerer Liste, siehe oben) - kein Null-Fallback wie in der normalen Retry-Schleife noetig.
        accepted.Sort((a, b) => a.Badness.CompareTo(b.Badness));
        var primary = accepted[0].Result;
        var alternatives = accepted.Skip(1).Select(a => a.Result).ToList();

        var warnings = primary.Warnings.ToList();
        if (accepted.Count < AlternativeTargetCount)
        {
            warnings.Add(new RouteWarning
            {
                Message = $"Es konnten nur {accepted.Count} statt {AlternativeTargetCount} " +
                          "ausreichend unterschiedliche Streckenvarianten gefunden werden.",
            });
        }

        return new RouteResult
        {
            Geometry = primary.Geometry,
            TotalDistanceMeters = primary.TotalDistanceMeters,
            EstimatedTotalTime = primary.EstimatedTotalTime,
            Warnings = warnings,
            Segments = primary.Segments,
            SurfaceSegments = primary.SurfaceSegments,
            SmoothnessSegments = primary.SmoothnessSegments,
            Wind = primary.Wind,
            Seed = primary.Seed,
            Alternatives = alternatives,
        };
    }

    /// <summary>Prueft, ob ein Kandidat spuerbar von einer bereits akzeptierten Route abweicht,
    /// statt im Wesentlichen dieselbe Schleife zu sein - samplet AlternativeGeometrySampleCount
    /// gleichmaessig verteilte Punkte entlang candidate und verlangt, dass mindestens
    /// MinAlternativeDivergenceFraction davon mehr als AlternativeDivergenceThresholdMeters von
    /// JEDER Stelle von accepted entfernt liegen.</summary>
    private static bool IsSufficientlyDifferent(IReadOnlyList<GeoPoint> candidate, IReadOnlyList<GeoPoint> accepted)
    {
        var length = PolylineMath.TotalLengthMeters(candidate);
        if (length <= 0)
            return false;

        var divergentSamples = 0;
        for (var i = 0; i < AlternativeGeometrySampleCount; i++)
        {
            var point = PolylineMath.PointAtDistance(candidate, length * i / (AlternativeGeometrySampleCount - 1.0));
            if (PolylineMath.MinDistanceToPolylineMeters(point, accepted) > AlternativeDivergenceThresholdMeters)
                divergentSamples++;
        }
        return divergentSamples / (double)AlternativeGeometrySampleCount >= MinAlternativeDivergenceFraction;
    }

    /// <summary>Bewertet, wie weit ein Versuch von den gesetzten Grenzwerten UND der geplanten
    /// Trainingsdauer entfernt ist - reine Extraktion der Badness-Formel aus der bestehenden
    /// Retry-Schleife (siehe CONCEPT.md Bugfix-Abschnitte zu Untergrund-Limits/Pflicht-
    /// Wegpunkten), jetzt auch von BuildRouteWithAlternativesAsync genutzt.</summary>
    private (double Badness, double UnpavedTotalMeters, double MaxUnpavedSegmentMeters, double RoughTotalMeters, int JunctionCount, double DurationMismatchSeconds) ScoreAttempt(
        RouteRequest request, RouteResult result, TimeSpan prescribedTime)
    {
        var (unpavedTotalMeters, maxUnpavedSegmentMeters) = SumBadSegments(result.SurfaceSegments, SurfaceClassifier.IsUnpaved);
        var (roughTotalMeters, _) = SumBadSegments(result.SmoothnessSegments, SurfaceClassifier.IsBadSmoothness);
        var junctionCount = corridorIndex.CountDisruptiveJunctionsNear(result.Geometry, JunctionProximityMeters);
        var durationMismatchSeconds = Math.Abs((result.EstimatedTotalTime - prescribedTime).TotalSeconds);

        var badness = unpavedTotalMeters + roughTotalMeters + junctionCount * JunctionBadnessWeightMeters
            + durationMismatchSeconds * DurationMismatchBadnessWeightMetersPerSecond;
        return (badness, unpavedTotalMeters, maxUnpavedSegmentMeters, roughTotalMeters, junctionCount, durationMismatchSeconds);
    }

    private async Task<RouteResult> BuildRouteAttemptAsync(RouteRequest request, int roundTripSeed, WindConditions? wind, CancellationToken ct)
    {
        var warnings = new List<RouteWarning>();
        var maxApproachRadiusMeters = ComputeMaxApproachRadiusMeters(request);

        var (roughLoop, stepDistances, stepGradients, stepHeadwinds) = await RefineRoughLoopAsync(request, roundTripSeed, wind, ct);
        var roughLoopLength = PolylineMath.TotalLengthMeters(roughLoop.Geometry);
        var totalDistance = stepDistances.Sum();

        // Jede Gruppe traegt die Position (0..1) entlang der groben Rundtour-Form, an der sie
        // in die finalen Wegpunkte einsortiert werden soll - Korridor-Start/Ende UND
        // Pflicht-Wegpunkte landen so in einer gemeinsamen, sinnvollen Reihenfolge (siehe
        // CONCEPT.md 6.19), statt dass letztere die Route auf einen Umweg zwingen.
        var waypointGroups = new List<(double Fraction, List<GeoPoint> Points)>();
        var reuseCache = new Dictionary<(double PowerBucket, double ScoreBucket), Corridor>();
        var segments = new List<RouteSegment>();
        // Pro Schritt der gefundene Korridor (oder null) - fuer EstimateTotalTime, siehe dort.
        var stepCorridors = new Corridor?[request.Plan.Steps.Count];
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
                step, stepDistance, targetPoint, request.SegmentReuse, request.AllowUTurns, reuseCache, maxApproachRadiusMeters, warnings);
            if (corridor is not null)
            {
                stepCorridors[i] = corridor;
                // Korridor-Geometrie direkt uebernehmen statt aus der finalen Route
                // herauszuschneiden - GraphHopper folgt zwischen exakt diesen zwei Punkten
                // ohnehin derselben Strecke, da wir sie ja genau deswegen gewaehlt haben.
                waypointGroups.Add((stepStartFraction, [corridor.Start, corridor.End]));
                segments.Add(new RouteSegment { Label = step.Label ?? "Intervall", Geometry = corridor.Geometry });
            }
        }

        foreach (var requiredPoint in request.RequiredPoints)
        {
            var distanceAlong = PolylineMath.NearestPointDistanceAlongMeters(roughLoop.Geometry, requiredPoint);
            var fraction = roughLoopLength <= 0 ? 0 : distanceAlong / roughLoopLength;
            waypointGroups.Add((fraction, [requiredPoint]));
        }

        // Zusaetzliche Anker aus der roughLoop-Geometrie zwischen den "echten" Wegpunkten
        // einstreuen (siehe AddRoughLoopAnchors) - ohne sie wuerde RouteThroughWaypointsAsync bei
        // duennen Wegpunkt-Listen (z.B. genau EIN Pflicht-Wegpunkt ohne jeden Effort-Schritt) den
        // kuerzesten Weg zwischen den wenigen echten Punkten waehlen, was bei nur zwei Punkten
        // schlicht "hin und auf demselben Weg zurueck" bedeutet - komplett unabhaengig von der
        // eigentlich gewuenschten Trainingsdistanz. Live gemeldeter Bug: "Route geht nur zum
        // Pflicht-Wegpunkt und nicht weiter" (siehe CONCEPT.md Bugfix-Abschnitt).
        var waypoints = new List<GeoPoint> { request.StartPoint };
        // Anker nur einstreuen, wenn ueberhaupt ein "echter" Wegpunkt vorliegt - sonst wuerde
        // dieser Schritt selbst bei einem voellig unbeschraenkten Plan (kein Effort-Schritt, kein
        // Pflicht-Wegpunkt) faelschlich RouteThroughWaypointsAsync statt des reinen,
        // distanz-korrekten round_trip-Ergebnisses erzwingen (waypoints.Count waere dann > 2,
        // obwohl es gar keine Wegpunkt-Vorgabe gibt).
        if (waypointGroups.Count > 0)
        {
            var previousFraction = 0.0;
            foreach (var group in waypointGroups.OrderBy(g => g.Fraction))
            {
                AddRoughLoopAnchors(waypoints, roughLoop.Geometry, roughLoopLength, previousFraction, group.Fraction);
                waypoints.AddRange(group.Points);
                previousFraction = group.Fraction;
            }
            AddRoughLoopAnchors(waypoints, roughLoop.Geometry, roughLoopLength, previousFraction, 1.0);
        }
        waypoints.Add(request.StartPoint);

        var finalRoute = waypoints.Count > 2
            ? await graphHopper.RouteThroughWaypointsAsync(waypoints, request.BlockedAreas, request.ConstructionClosures, ct)
            : roughLoop;

        // finalRoute.Time ist GraphHoppers EIGENE Schaetzung (fester 25 km/h Profil-Speed, siehe
        // CONCEPT.md 6.17) - hat nichts mit dem Fahrerprofil/den Zonenleistungen zu tun. Die
        // tatsaechlich angezeigte "Geschaetzte Zeit" wird stattdessen ueber unser eigenes
        // physikbasiertes Modell rekonstruiert, siehe EstimateTotalTime (CONCEPT.md 6.23/7).
        var estimatedTotalTime = EstimateTotalTime(request, finalRoute.DistanceMeters, stepDistances, stepGradients, stepHeadwinds, stepCorridors);

        CheckApproachBudget(request, estimatedTotalTime, warnings);
        if (!request.AllowUTurns)
            CheckForUTurns(finalRoute, warnings);

        return new RouteResult
        {
            Geometry = finalRoute.Geometry,
            TotalDistanceMeters = finalRoute.DistanceMeters,
            EstimatedTotalTime = estimatedTotalTime,
            Warnings = warnings,
            Segments = segments,
            SurfaceSegments = finalRoute.SurfaceSegments,
            SmoothnessSegments = finalRoute.SmoothnessSegments,
            Wind = wind,
            Seed = roundTripSeed,
            Alternatives = [],
        };
    }

    /// <summary>Rekonstruiert die Gesamtzeit ueber das eigene physikbasierte Modell statt
    /// GraphHoppers `time`-Feld zu uebernehmen (siehe CONCEPT.md 6.23/7 - GraphHoppers Wert
    /// nutzt einen festen 25 km/h Profil-Speed, unabhaengig vom tatsaechlichen Fahrerprofil).
    /// Effort-Schritte MIT gefundenem Korridor nutzen dessen TATSAECHLICHE Laenge (kann von der
    /// urspruenglichen Schaetzung abweichen, z.B. durch die Fallback-Eskalation in
    /// FindCorridorWithFallback). Alle uebrigen Schritte (ruhige Bloecke UND Effort-Schritte ohne
    /// gefundenen Korridor) teilen sich die tatsaechlich VERBLEIBENDE Distanz
    /// (finalRoute.DistanceMeters abzueglich aller Korridor-Laengen) proportional zu ihrem
    /// urspruenglich geschaetzten Anteil auf - das faengt GraphHoppers round_trip-Ungenauigkeit
    /// (die tatsaechliche Distanz weicht oft von der angeforderten ab, siehe CONCEPT.md 6.23) mit
    /// ein, statt naiv die Schaetzungen aus RefineRoughLoopAsync unveraendert aufzusummieren.</summary>
    private TimeSpan EstimateTotalTime(
        RouteRequest request,
        double actualTotalDistanceMeters,
        double[] stepDistances,
        double[] stepGradients,
        double[] stepHeadwinds,
        Corridor?[] stepCorridors)
    {
        var steps = request.Plan.Steps;
        var corridorDistanceMeters = 0.0;
        var corridorTimeSeconds = 0.0;
        var remainingOriginalDistanceMeters = 0.0;

        for (var i = 0; i < steps.Count; i++)
        {
            var corridor = stepCorridors[i];
            if (corridor is null)
            {
                remainingOriginalDistanceMeters += stepDistances[i];
                continue;
            }
            corridorDistanceMeters += corridor.LengthMeters;
            corridorTimeSeconds += TimeForDistanceOrPrescribed(
                corridor.LengthMeters, steps[i], request.Rider, stepGradients[i], stepHeadwinds[i]).TotalSeconds;
        }

        var remainingActualDistanceMeters = Math.Max(0.0, actualTotalDistanceMeters - corridorDistanceMeters);
        var remainingTimeSeconds = 0.0;
        for (var i = 0; i < steps.Count; i++)
        {
            if (stepCorridors[i] is not null)
                continue;
            var share = remainingOriginalDistanceMeters <= 0 ? 0.0 : stepDistances[i] / remainingOriginalDistanceMeters;
            var allocatedDistanceMeters = remainingActualDistanceMeters * share;
            remainingTimeSeconds += TimeForDistanceOrPrescribed(
                allocatedDistanceMeters, steps[i], request.Rider, stepGradients[i], stepHeadwinds[i]).TotalSeconds;
        }

        return TimeSpan.FromSeconds(corridorTimeSeconds + remainingTimeSeconds);
    }

    // PowerSpeedModel.TimeForDistance liefert TimeSpan.MaxValue bei nicht erreichbarer
    // Geschwindigkeit (z.B. 0 Watt) - dessen TotalSeconds in eine Summe einfliessen zu lassen
    // wuerde diese unbrauchbar machen (Ueberlauf/absurd hoher Wert). Faellt in diesem seltenen
    // Randfall auf die reine Plandauer des Schritts zurueck statt die gesamte Zeitschaetzung zu
    // zerstoeren.
    private TimeSpan TimeForDistanceOrPrescribed(
        double distanceMeters, TrainingStep step, RiderProfile profile, double gradient, double headwindMps)
    {
        var time = powerModel.TimeForDistance(distanceMeters, step.TargetPowerWatts, profile, gradient, headwindMps);
        return time == TimeSpan.MaxValue ? step.Duration : time;
    }

    /// <summary>Summiert die Laenge aller "schlechten" Segmente einer Liste (Oberflaechen- ODER
    /// Smoothness-Segmente, je nach <paramref name="isBad"/>) sowie die Laenge des laengsten
    /// einzelnen zusammenhaengenden Segments. Bewusst getrennte Aufrufe fuer surface- und
    /// smoothness-Segmente statt einer kombinierten Zahl (siehe CONCEPT.md Bugfix-Abschnitt zu
    /// ueberhoehten Warnungs-Zahlen): "unbefestigt" (surface=unpaved/gravel/...) und "rauer
    /// Belag" (smoothness=bad auf einem an sich befestigten Untergrund, z.B. rissiger alter
    /// Asphalt) sind fachlich unterschiedliche Dinge und bekommen daher eigene Limits/Zahlen
    /// statt gemeinsam ins "unbefestigt"-Limit einzufliessen.</summary>
    private static (double Total, double MaxSegment) SumBadSegments(
        IReadOnlyList<SurfaceSegment> segments, Func<string, bool> isBad)
    {
        var total = 0.0;
        var maxSegment = 0.0;
        foreach (var segment in segments)
        {
            if (!isBad(segment.Surface))
                continue;
            var length = PolylineMath.TotalLengthMeters(segment.Geometry);
            total += length;
            maxSegment = Math.Max(maxSegment, length);
        }
        return (total, maxSegment);
    }

    /// <summary>Fuegt zwischen zwei Fraktionen entlang der roughLoop-Geometrie zusaetzliche
    /// "Anker"-Wegpunkte ein (im Abstand RoughLoopAnchorSpacingMeters), damit
    /// RouteThroughWaypointsAsync zwischen zwei "echten" Wegpunkten (Korridor-Start/Ende,
    /// Pflicht-Wegpunkte) ungefaehr der bereits distanz-korrekten Schleifenform folgt, statt den
    /// kuerzesten (und damit potenziell viel zu kurzen) Weg zu waehlen. Ohne Anker wuerde z.B.
    /// ein einzelner Pflicht-Wegpunkt ohne jeden Effort-Schritt dazu fuehren, dass die Route
    /// einfach zu diesem Punkt hin- und auf demselben Weg zurueckfaehrt.</summary>
    private static void AddRoughLoopAnchors(
        List<GeoPoint> waypoints, IReadOnlyList<GeoPoint> roughLoopGeometry, double roughLoopLength,
        double fromFraction, double toFraction)
    {
        if (roughLoopLength <= 0 || toFraction <= fromFraction)
            return;

        var gapMeters = (toFraction - fromFraction) * roughLoopLength;
        var anchorCount = (int)(gapMeters / RoughLoopAnchorSpacingMeters);
        for (var i = 1; i <= anchorCount; i++)
        {
            var fraction = fromFraction + (toFraction - fromFraction) * i / (anchorCount + 1);
            waypoints.Add(PolylineMath.PointAtDistance(roughLoopGeometry, fraction * roughLoopLength));
        }
    }

    /// <summary>Fragt round_trip an, verfeinert die pro-Schritt-Distanzen anhand des
    /// tatsaechlichen Hoehenprofils der Antwort (statt der reinen Flach-Annahme aus 3.3) und
    /// fragt bei signifikanter Gesamtabweichung erneut an - der "iterative Prozess" aus 3.3.
    /// Die Position jedes Schritts entlang der Schleife wird pro Iteration anhand der
    /// Distanzen der VORHERIGEN Iteration bestimmt (nicht der gerade neu berechneten) - sonst
    /// haengt die Positionsbestimmung von einem Wert ab, den sie selbst gerade erst liefert.</summary>
    private async Task<(GraphHopperRoute RoughLoop, double[] StepDistances, double[] StepGradients, double[] StepHeadwinds)> RefineRoughLoopAsync(
        RouteRequest request, int roundTripSeed, WindConditions? wind, CancellationToken ct)
    {
        var steps = request.Plan.Steps;
        // Erste (flache) Schaetzung kennt noch keine Streckengeometrie, kann also weder
        // Steigung noch Fahrtrichtung/Windkomponente beruecksichtigen - wie gradient=0 wird auch
        // headwind=0 erst in den folgenden Iterationen anhand der tatsaechlichen Route verfeinert.
        var stepDistances = steps.Select(s => EstimateDistance(s, request.Rider, gradient: 0.0, headwindMps: 0.0)).ToArray();
        // Gradient/Wind der LETZTEN Iteration werden an EstimateTotalTime weitergereicht (siehe
        // dort), damit die abschliessende Zeitschaetzung dieselben Umgebungsbedingungen nutzt,
        // die auch die Distanzschaetzung bestimmt haben - initial 0, falls unten nie ueberschrieben
        // (z.B. bei einem einzigen, sofort konvergenten Schritt ohne Schleifendurchlauf).
        var stepGradients = new double[steps.Count];
        var stepHeadwinds = new double[steps.Count];
        var totalDistance = stepDistances.Sum();

        var roughLoop = await graphHopper.RoundTripAsync(request.StartPoint, totalDistance, roundTripSeed, request.BlockedAreas, request.ConstructionClosures, ct);

        for (var iteration = 0; iteration < MaxDistanceRefinementIterations; iteration++)
        {
            var roughLoopLength = PolylineMath.TotalLengthMeters(roughLoop.Geometry);
            var refined = new double[steps.Count];
            var cumulative = 0.0;

            for (var i = 0; i < steps.Count; i++)
            {
                var fraction = totalDistance <= 0 ? 0 : cumulative / totalDistance;
                var gradient = PolylineMath.AverageGradient(roughLoop.Geometry, fraction * roughLoopLength, GradientSampleWindowMeters);
                var headwindMps = 0.0;
                if (wind is not null)
                {
                    var bearing = PolylineMath.AverageBearingDegrees(roughLoop.Geometry, fraction * roughLoopLength, BearingSampleWindowMeters);
                    headwindMps = ComputeHeadwindComponentMps(bearing, wind);
                }
                stepGradients[i] = gradient;
                stepHeadwinds[i] = headwindMps;
                refined[i] = EstimateDistance(steps[i], request.Rider, gradient, headwindMps);
                cumulative += stepDistances[i];
            }

            var refinedTotal = refined.Sum();
            var relativeChange = totalDistance <= 0 ? 0 : Math.Abs(refinedTotal - totalDistance) / totalDistance;

            stepDistances = refined;
            totalDistance = refinedTotal;

            if (relativeChange < DistanceRefinementToleranceFraction)
                break;

            roughLoop = await graphHopper.RoundTripAsync(request.StartPoint, totalDistance, roundTripSeed, request.BlockedAreas, request.ConstructionClosures, ct);
        }

        return (roughLoop, stepDistances, stepGradients, stepHeadwinds);
    }

    private double EstimateDistance(TrainingStep step, RiderProfile profile, double gradient, double headwindMps) =>
        powerModel.SolveSpeedMps(step.TargetPowerWatts, profile, gradient, headwindMps) * step.Duration.TotalSeconds;

    /// <summary>Windkomponente entgegen der Fahrtrichtung (positiv=Gegenwind, negativ=
    /// Rueckenwind) aus Peilung und Windrichtung - siehe PowerSpeedModel.SolveSpeedMps und
    /// CONCEPT.md Phase-4-Backlog "Windmodellierung". WindFromDirectionDegrees folgt
    /// meteorologischer Konvention (Richtung, AUS der der Wind weht) - deckt sich die
    /// Fahrtrichtung mit dieser Richtung (Winkel-Differenz 0), faehrt man direkt in den Wind
    /// hinein, cos(0)=1 ergibt vollen Gegenwind. Bei 180 Grad Differenz (Fahrtrichtung = wohin
    /// der Wind weht) ergibt cos(180)=-1 vollen Rueckenwind.</summary>
    private static double ComputeHeadwindComponentMps(double travelBearingDegrees, WindConditions wind)
    {
        var angleDiffRad = (travelBearingDegrees - wind.WindFromDirectionDegrees) * Math.PI / 180.0;
        return wind.WindSpeedMps * Math.Cos(angleDiffRad);
    }

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
        bool allowUTurns,
        Dictionary<(double, double), Corridor> reuseCache,
        double maxSearchRadiusMeters,
        List<RouteWarning> warnings)
    {
        // Exaktes Wiederverwenden bedeutet: Route faehrt vom Korridorende direkt zurueck zum
        // -anfang - ohne alternative Strecke ist das meist nur per Kehrtwende moeglich. Bei
        // AllowUTurns=false daher lieber frisch suchen (naeher an "Streckenvielfalt"), auch
        // wenn eigentlich "Gleicher Ort" gewuenscht ist.
        var effectiveReuse = allowUTurns ? reusePreference : SegmentReusePreference.PreferVariety;
        var cacheKey = (Math.Round(step.TargetPowerWatts / 10) * 10, step.MaxDisruptionScore);
        if (effectiveReuse == SegmentReusePreference.PreferReuse && reuseCache.TryGetValue(cacheKey, out var cached))
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
                return CacheIfPreferred(found, cacheKey, effectiveReuse, reuseCache);
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
            return CacheIfPreferred(bestEffort, cacheKey, effectiveReuse, reuseCache);
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

    /// <summary>Erkennt abrupte Richtungswechsel in der finalen Route (Naeherung fuer
    /// Kehrtwenden) und meldet sie transparent - kann eine Kehrtwende in duennen
    /// Strassennetzen (z.B. echte Sackgassen) nicht immer verhindern, siehe RouteRequest.
    /// AllowUTurns.</summary>
    private static void CheckForUTurns(GraphHopperRoute finalRoute, List<RouteWarning> warnings)
    {
        foreach (var location in PolylineMath.DetectSharpReversals(finalRoute.Geometry))
        {
            warnings.Add(new RouteWarning
            {
                Message = "Die Route enthält an dieser Stelle vermutlich eine Kehrtwende - " +
                          "im aktuellen Straßennetz war keine Alternative ohne Umkehren auffindbar.",
                Location = location,
            });
        }
    }

    // Prueft die Gesamtdauer der finalen Route GEGEN DEN TRAININGSPLAN in BEIDE Richtungen -
    // zu lang (bestehende Pruefung: die Anfahrt/Korridor-Umwege ueberschreiten das
    // Anfahrt-Budget aus 4.4) UND zu KURZ (fehlte urspruenglich: ein round_trip-Seed kann eine
    // deutlich kuerzere Schleife liefern als angefordert, ohne dass GraphHopper das je meldet -
    // live beobachtet als 24 km/58 min statt angeforderter ~120 min GA1, siehe auch
    // DurationMismatchBadnessWeightMetersPerSecond). Dieselbe MaxApproachMinutes-Toleranz wird
    // fuer beide Richtungen als symmetrisches Toleranzband um die Plandauer verwendet.
    private static void CheckApproachBudget(RouteRequest request, TimeSpan estimatedTotalTime, List<RouteWarning> warnings)
    {
        var prescribedTicks = request.Plan.Steps.Sum(s => s.Duration.Ticks);
        var prescribedTime = TimeSpan.FromTicks(prescribedTicks);
        var extraTime = estimatedTotalTime - prescribedTime;
        if (extraTime.TotalMinutes > request.MaxApproachMinutes)
        {
            warnings.Add(new RouteWarning
            {
                Message = $"Route ist {extraTime.TotalMinutes:F0} min länger als der reine Trainingsplan " +
                          $"(Budget: {request.MaxApproachMinutes:F0} min) - die nächste geeignete Strecke liegt weiter entfernt.",
            });
        }
        else if (-extraTime.TotalMinutes > request.MaxApproachMinutes)
        {
            warnings.Add(new RouteWarning
            {
                Message = $"Route ist {-extraTime.TotalMinutes:F0} min KÜRZER als der reine Trainingsplan " +
                          $"({request.Plan.Steps.Sum(s => s.Duration.TotalMinutes):F0} min) - im verfügbaren Straßennetz " +
                          "konnte keine ausreichend lange Streckenführung gefunden werden, die auch die übrigen " +
                          "gesetzten Grenzwerte einhält.",
            });
        }
    }
}
