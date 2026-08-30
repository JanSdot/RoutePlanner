using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.PowerModel;
using TrainingRoutePlanner.RouteEngine;
using Xunit;

namespace TrainingRoutePlanner.Tests;

public class RouteConstructionServiceTests
{
    private static readonly RiderProfile Rider = new()
    {
        FtpWatts = 250,
        WeightKg = 75,
        SprintAvgWatts = 800,
    };

    private static readonly GeoPoint Start = new(52.5426187, 13.4763778);

    private sealed class FakeGraphHopperClient : IGraphHopperClient
    {
        public List<IReadOnlyList<GeoPoint>> WaypointCalls { get; } = [];
        public List<double> RoundTripDistanceRequests { get; } = [];
        public List<int> RoundTripSeeds { get; } = [];
        public double RoundTripDistanceMeters { get; set; } = 20_000;

        // Default = 0, damit Tests, die die Anfahrt-Budget-Pruefung nicht betreffen, nicht
        // versehentlich eine Warnung ausloesen (extra Zeit wird dann negativ, nie > Budget).
        public TimeSpan FinalRouteTime { get; set; } = TimeSpan.Zero;

        // Erlaubt Tests, ein Hoehenprofil in Abhaengigkeit von der angefragten Distanz zu
        // simulieren (fuer die Refinement-Tests) - Default: flache Platzhalter-Schleife.
        public Func<double, IReadOnlyList<GeoPoint>>? GeometryFactory { get; set; }

        // Erlaubt Tests, unterschiedliche Untergrund-Ergebnisse je nach round_trip-Seed zu
        // simulieren (fuer die Untergrund-Vermeidungs-Tests) - Default: kein Untergrund-Anteil.
        public Func<int, IReadOnlyList<SurfaceSegment>>? SurfaceSegmentsBySeed { get; set; }

        public List<IReadOnlyList<BlockedArea>> BlockedAreasReceived { get; } = [];

        public Task<GraphHopperRoute> RoundTripAsync(
            GeoPoint start, double distanceMeters, int seed, IReadOnlyList<BlockedArea> blockedAreas, CancellationToken ct = default)
        {
            RoundTripDistanceRequests.Add(distanceMeters);
            RoundTripSeeds.Add(seed);
            BlockedAreasReceived.Add(blockedAreas);

            var geometry = GeometryFactory?.Invoke(distanceMeters) ?? new List<GeoPoint>
            {
                start,
                new(start.Lat + 0.05, start.Lon),
                new(start.Lat + 0.05, start.Lon + 0.05),
                new(start.Lat, start.Lon + 0.05),
                start,
            };
            var surfaceSegments = SurfaceSegmentsBySeed?.Invoke(seed) ?? [];
            return Task.FromResult(new GraphHopperRoute(distanceMeters, TimeSpan.FromSeconds(distanceMeters / 8.0), geometry, surfaceSegments));
        }

        public Task<GraphHopperRoute> RouteThroughWaypointsAsync(
            IReadOnlyList<GeoPoint> waypoints, IReadOnlyList<BlockedArea> blockedAreas, CancellationToken ct = default)
        {
            WaypointCalls.Add(waypoints);
            BlockedAreasReceived.Add(blockedAreas);
            return Task.FromResult(new GraphHopperRoute(RoundTripDistanceMeters, FinalRouteTime, waypoints, []));
        }
    }

    private sealed class FakeCorridorIndex : ICorridorIndex
    {
        public List<(GeoPoint Near, double MinLength, double MaxScore, double Radius)> Calls { get; } = [];
        public Func<(GeoPoint Near, double MinLength, double MaxScore, double Radius), Corridor?>? Responder { get; set; }

        // Erlaubt Tests, die Anzahl gefundener Ampel-/Stopp-Kreuzungen unabhaengig von der
        // tatsaechlichen Routengeometrie zu simulieren - Default: nie eine Kreuzung.
        public Func<IReadOnlyList<GeoPoint>, int>? JunctionCountResponder { get; set; }

        public int CountDisruptiveJunctionsNear(IReadOnlyList<GeoPoint> routeGeometry, double proximityMeters) =>
            JunctionCountResponder?.Invoke(routeGeometry) ?? 0;

        public Corridor? TryFindCorridor(GeoPoint near, double minLengthMeters, double maxDisruptionScore, double searchRadiusMeters)
        {
            var call = (near, minLengthMeters, maxDisruptionScore, searchRadiusMeters);
            Calls.Add(call);
            return Responder?.Invoke(call);
        }
    }

    private static Corridor MakeCorridor(double length = 1500, double score = 1.0) => new()
    {
        Start = new GeoPoint(52.6, 13.5),
        End = new GeoPoint(52.61, 13.5),
        LengthMeters = length,
        DisruptionScore = score,
        Geometry = [new GeoPoint(52.6, 13.5), new GeoPoint(52.61, 13.5)],
    };

    private static RouteRequest MakeRequest(IEnumerable<TrainingStep> steps, SegmentReusePreference reuse = SegmentReusePreference.PreferReuse) => new()
    {
        StartPoint = Start,
        Rider = Rider,
        Plan = new TrainingPlan { Steps = steps.ToList() },
        SegmentReuse = reuse,
    };

    [Fact]
    public async Task QuietStep_IsSkipped_NoCorridorLookup()
    {
        var corridors = new FakeCorridorIndex { Responder = _ => MakeCorridor() };
        var service = new RouteConstructionService(new FakeGraphHopperClient(), corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.GA1, TimeSpan.FromMinutes(30), Rider);
        await service.BuildRouteAsync(MakeRequest([step]));

        Assert.Empty(corridors.Calls);
    }

    [Fact]
    public async Task EffortStep_TriggersCorridorLookup_WithExpectedLength()
    {
        var corridors = new FakeCorridorIndex { Responder = _ => MakeCorridor() };
        var powerModel = new PowerSpeedModel();
        var service = new RouteConstructionService(new FakeGraphHopperClient(), corridors, powerModel);

        var step = ZoneResolver.FromZone(TrainingZone.VO2max, TimeSpan.FromMinutes(5), Rider);
        var expectedLength = powerModel.SolveSpeedMps(step.TargetPowerWatts, Rider) * step.Duration.TotalSeconds;

        var result = await service.BuildRouteAsync(MakeRequest([step]));

        var call = Assert.Single(corridors.Calls);
        Assert.Equal(expectedLength, call.MinLength, precision: 3);
        Assert.Equal(step.MaxDisruptionScore, call.MaxScore, precision: 6);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task FallbackEscalation_WidensRadiusAndScore_UntilFound()
    {
        var corridors = new FakeCorridorIndex();
        var attempt = 0;
        corridors.Responder = _ =>
        {
            attempt++;
            return attempt >= 3 ? MakeCorridor() : null; // erst beim 3. (gelockerten) Versuch ein Treffer
        };
        var service = new RouteConstructionService(new FakeGraphHopperClient(), corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.EB, TimeSpan.FromMinutes(5), Rider);
        var result = await service.BuildRouteAsync(MakeRequest([step]));

        Assert.Equal(3, corridors.Calls.Count);
        Assert.True(corridors.Calls[1].Radius > corridors.Calls[0].Radius, "Suchradius sollte wachsen");
        Assert.True(corridors.Calls[1].MaxScore > corridors.Calls[0].MaxScore, "Score-Schwelle sollte sich lockern");
        Assert.Contains(result.Warnings, w => w.Message.Contains("gelockert"));
    }

    [Fact]
    public async Task AllStrictAttemptsFail_BestEffortCandidateUsed()
    {
        var corridors = new FakeCorridorIndex();
        corridors.Responder = call => call.MaxScore >= 100 ? MakeCorridor(length: 300, score: 8.0) : null;
        var service = new RouteConstructionService(new FakeGraphHopperClient(), corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.VO2max, TimeSpan.FromMinutes(5), Rider);
        var result = await service.BuildRouteAsync(MakeRequest([step]));

        Assert.Contains(result.Warnings, w => w.Message.Contains("bestmögliche"));
    }

    [Fact]
    public async Task NothingFoundAtAll_StepSkipped_ClearWarning()
    {
        var corridors = new FakeCorridorIndex { Responder = _ => null };
        var service = new RouteConstructionService(new FakeGraphHopperClient(), corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.VO2max, TimeSpan.FromMinutes(5), Rider);
        var result = await service.BuildRouteAsync(MakeRequest([step]));

        Assert.Contains(result.Warnings, w => w.Message.Contains("konnte kein Korridor gefunden werden") && !w.Message.Contains("bestmögliche"));
    }

    [Fact]
    public async Task SegmentReuse_PreferReuse_QueriesCorridorIndexOnlyOnce()
    {
        var corridors = new FakeCorridorIndex { Responder = _ => MakeCorridor() };
        var service = new RouteConstructionService(new FakeGraphHopperClient(), corridors, new PowerSpeedModel());

        var step1 = ZoneResolver.FromZone(TrainingZone.EB, TimeSpan.FromMinutes(5), Rider);
        var step2 = ZoneResolver.FromZone(TrainingZone.EB, TimeSpan.FromMinutes(5), Rider);
        var request = MakeRequest([step1, step2], SegmentReusePreference.PreferReuse);

        await service.BuildRouteAsync(request);

        Assert.Single(corridors.Calls);
    }

    [Fact]
    public async Task SegmentReuse_PreferVariety_QueriesCorridorIndexForEachStep()
    {
        var corridors = new FakeCorridorIndex { Responder = _ => MakeCorridor() };
        var service = new RouteConstructionService(new FakeGraphHopperClient(), corridors, new PowerSpeedModel());

        var step1 = ZoneResolver.FromZone(TrainingZone.EB, TimeSpan.FromMinutes(5), Rider);
        var step2 = ZoneResolver.FromZone(TrainingZone.EB, TimeSpan.FromMinutes(5), Rider);
        var request = MakeRequest([step1, step2], SegmentReusePreference.PreferVariety);

        await service.BuildRouteAsync(request);

        Assert.Equal(2, corridors.Calls.Count);
    }

    [Fact]
    public async Task ApproachBudgetExceeded_AddsWarning()
    {
        var corridors = new FakeCorridorIndex { Responder = _ => MakeCorridor() };
        var ghClient = new FakeGraphHopperClient { FinalRouteTime = TimeSpan.FromMinutes(90) };
        var service = new RouteConstructionService(ghClient, corridors, new PowerSpeedModel());

        // Effort-Schritt noetig, damit RouteThroughWaypointsAsync (und damit FinalRouteTime)
        // ueberhaupt greift - ein reiner GA1-Plan wuerde nie ueber den Wegpunkt-Pfad laufen.
        var step = ZoneResolver.FromZone(TrainingZone.EB, TimeSpan.FromMinutes(5), Rider);

        var result = await service.BuildRouteAsync(new RouteRequest
        {
            StartPoint = Start,
            Rider = Rider,
            Plan = new TrainingPlan { Steps = [step] },
            MaxApproachMinutes = 30,
        });

        Assert.Contains(result.Warnings, w => w.Message.Contains("länger als der reine Trainingsplan"));
    }

    [Fact]
    public async Task UphillGradient_ReducesDistanceEstimate_AndTriggersRefinementRequest()
    {
        var ghClient = new FakeGraphHopperClient
        {
            // Konstante 5% Steigung ueber die gesamte angefragte Distanz - unabhaengig von
            // der Distanz reproduzierbar, damit der Test deterministisch bleibt.
            GeometryFactory = distanceMeters => new List<GeoPoint>
            {
                new(Start.Lat, Start.Lon, 0),
                new(Start.Lat + 0.05, Start.Lon + 0.05, distanceMeters * 0.05),
            },
        };
        var corridors = new FakeCorridorIndex { Responder = _ => MakeCorridor() };
        var service = new RouteConstructionService(ghClient, corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.EB, TimeSpan.FromMinutes(5), Rider);
        await service.BuildRouteAsync(MakeRequest([step]));

        Assert.True(ghClient.RoundTripDistanceRequests.Count >= 2,
            "Bei signifikanter Steigung sollte eine verfeinerte Zweitanfrage erfolgen");
        Assert.True(ghClient.RoundTripDistanceRequests[1] < ghClient.RoundTripDistanceRequests[0],
            "Bergauf sollte die verfeinerte Distanz kleiner sein als die Flach-Schaetzung (weniger Weg in gleicher Zeit)");
    }

    [Fact]
    public async Task ApproachBudget_CapsCorridorSearchRadius()
    {
        var corridors = new FakeCorridorIndex { Responder = _ => null };
        var powerModel = new PowerSpeedModel();
        var service = new RouteConstructionService(new FakeGraphHopperClient(), corridors, powerModel);

        var step = ZoneResolver.FromZone(TrainingZone.EB, TimeSpan.FromMinutes(5), Rider);
        var request = new RouteRequest
        {
            StartPoint = Start,
            Rider = Rider,
            Plan = new TrainingPlan { Steps = [step] },
            MaxApproachMinutes = 1, // sehr eng, damit der Deckel unterhalb der Standard-Suchradien liegt
        };

        await service.BuildRouteAsync(request);

        var quietStep = ZoneResolver.FromZone(TrainingZone.GA1, TimeSpan.FromMinutes(1), Rider);
        var quietSpeedMps = powerModel.SolveSpeedMps(quietStep.TargetPowerWatts, Rider);
        var expectedCap = quietSpeedMps * (1 * 60.0 / 2.0);

        Assert.NotEmpty(corridors.Calls);
        Assert.All(corridors.Calls, call => Assert.True(call.Radius <= expectedCap + 0.01,
            $"Suchradius {call.Radius} überschreitet den Anfahrt-Budget-Deckel {expectedCap}"));
    }

    [Fact]
    public async Task EffortStepWithCorridor_IsReturnedAsLabeledSegment()
    {
        var corridor = MakeCorridor();
        var corridors = new FakeCorridorIndex { Responder = _ => corridor };
        var service = new RouteConstructionService(new FakeGraphHopperClient(), corridors, new PowerSpeedModel());

        var quietStep = ZoneResolver.FromZone(TrainingZone.GA1, TimeSpan.FromMinutes(20), Rider);
        var workStep = ZoneResolver.FromFtpPercent(115, TimeSpan.FromMinutes(3), Rider, label: "Work");
        var result = await service.BuildRouteAsync(MakeRequest([quietStep, workStep]));

        var segment = Assert.Single(result.Segments);
        Assert.Equal("Work", segment.Label);
        Assert.Equal(corridor.Geometry, segment.Geometry);
    }

    [Fact]
    public async Task QuietOnlyPlan_HasNoSegments()
    {
        var corridors = new FakeCorridorIndex { Responder = _ => MakeCorridor() };
        var service = new RouteConstructionService(new FakeGraphHopperClient(), corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.GA1, TimeSpan.FromMinutes(30), Rider);
        var result = await service.BuildRouteAsync(MakeRequest([step]));

        Assert.Empty(result.Segments);
    }

    [Fact]
    public async Task AllowUTurnsFalse_DisablesExactReuse_ForRepeatedSteps()
    {
        var corridors = new FakeCorridorIndex { Responder = _ => MakeCorridor() };
        var service = new RouteConstructionService(new FakeGraphHopperClient(), corridors, new PowerSpeedModel());

        var step1 = ZoneResolver.FromZone(TrainingZone.EB, TimeSpan.FromMinutes(5), Rider);
        var step2 = ZoneResolver.FromZone(TrainingZone.EB, TimeSpan.FromMinutes(5), Rider);
        var request = new RouteRequest
        {
            StartPoint = Start,
            Rider = Rider,
            Plan = new TrainingPlan { Steps = [step1, step2] },
            SegmentReuse = SegmentReusePreference.PreferReuse,
            AllowUTurns = false,
        };

        await service.BuildRouteAsync(request);

        // Trotz "PreferReuse" wird bei AllowUTurns=false fuer jede Wiederholung frisch gesucht,
        // da sonst zwangsweise ein Rueckweg entlang desselben Korridors noetig waere.
        Assert.Equal(2, corridors.Calls.Count);
    }

    [Fact]
    public async Task AllowUTurnsFalse_FlagsSharpReversalInFinalGeometry()
    {
        var corridors = new FakeCorridorIndex { Responder = _ => MakeCorridor() };
        var ghClient = new FakeGraphHopperClient
        {
            // Gerade Strecke raus, dann eine scharfe Kehrtwende (~180 Grad) zurueck.
            GeometryFactory = _ =>
            [
                new GeoPoint(52.50, 13.50),
                new GeoPoint(52.51, 13.50),
                new GeoPoint(52.52, 13.50),
                new GeoPoint(52.51, 13.50),
                new GeoPoint(52.50, 13.50),
            ],
        };
        var service = new RouteConstructionService(ghClient, corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.EB, TimeSpan.FromMinutes(5), Rider);
        var request = new RouteRequest
        {
            StartPoint = Start,
            Rider = Rider,
            Plan = new TrainingPlan { Steps = [step] },
            AllowUTurns = false,
        };

        var result = await service.BuildRouteAsync(request);

        Assert.Contains(result.Warnings, w => w.Message.Contains("Kehrtwende"));
    }

    // GraphHopper liefert pro round_trip.seed eine andere Streckenfuehrung - die folgenden Tests
    // nutzen einen reinen GA1-Plan (keine Effort-Schritte -> keine Korridor-/Wegpunkt-Logik
    // beteiligt), damit ausschliesslich das Untergrund-Vermeidungs-Verhalten selbst geprueft wird.
    private static IReadOnlyList<SurfaceSegment> UnpavedSegmentOfLength(double approxMeters) =>
    [
        new SurfaceSegment
        {
            Surface = "gravel",
            // 1 Grad Breitengrad entspricht ca. 111.2 km - reicht fuer diese Tests, exakte
            // Genauigkeit ist nicht noetig, nur klare Groessenordnung ober-/unterhalb der
            // getesteten Grenzwerte.
            Geometry = [new GeoPoint(52.50, 13.50), new GeoPoint(52.50 + approxMeters / 111_200.0, 13.50)],
        },
    ];

    [Fact]
    public async Task NoUnpavedLimits_MakesOnlyOneRoundTripAttempt()
    {
        var corridors = new FakeCorridorIndex();
        var ghClient = new FakeGraphHopperClient { SurfaceSegmentsBySeed = _ => UnpavedSegmentOfLength(5000) };
        var service = new RouteConstructionService(ghClient, corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.GA1, TimeSpan.FromMinutes(30), Rider);
        await service.BuildRouteAsync(MakeRequest([step]));

        Assert.Single(ghClient.RoundTripSeeds);
    }

    [Fact]
    public async Task UnpavedTotalWithinLimit_StopsAtFirstSuccessfulSeed()
    {
        var corridors = new FakeCorridorIndex();
        var ghClient = new FakeGraphHopperClient
        {
            // Seed 1 ueberschreitet das Limit deutlich, Seed 2 erfuellt es bereits (400m < 500m) -
            // Seed 3 waere sogar noch besser (100m), darf aber nie angefragt werden: sobald ein
            // Versuch die Grenzwerte einhaelt, wird sofort abgebrochen statt auf einen noch
            // besseren spaeteren Versuch zu hoffen (siehe CONCEPT.md 6.12 - Zeitbudget-Risiko).
            SurfaceSegmentsBySeed = seed => seed switch
            {
                1 => UnpavedSegmentOfLength(2000),
                2 => UnpavedSegmentOfLength(400),
                _ => UnpavedSegmentOfLength(100),
            },
        };
        var service = new RouteConstructionService(ghClient, corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.GA1, TimeSpan.FromMinutes(30), Rider);
        var result = await service.BuildRouteAsync(new RouteRequest
        {
            StartPoint = Start,
            Rider = Rider,
            Plan = new TrainingPlan { Steps = [step] },
            MaxTotalUnpavedMeters = 500,
        });

        Assert.Equal([1, 2], ghClient.RoundTripSeeds);
        Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("Streckenvarianten"));
        // Seed 2 (400m) wurde verwendet, nicht Seed 3 (100m) - der ist nie angefragt worden.
        var expectedLat = 52.50 + 400.0 / 111_200.0;
        Assert.Equal(expectedLat, result.SurfaceSegments[0].Geometry[1].Lat, precision: 3);
    }

    [Fact]
    public async Task UnpavedLimitNeverSatisfied_TriesAllAttempts_UsesBestAttempt_AndWarns()
    {
        var corridors = new FakeCorridorIndex();
        // Jeder der (jetzt 10 statt vormals 5) Versuche liegt gleich weit ueber dem Limit - bei
        // einem Gleichstand bleibt der ERSTE gefundene Versuch der "beste" (striktes "<", keine
        // spaetere Ueberschreibung bei Gleichstand).
        var ghClient = new FakeGraphHopperClient { SurfaceSegmentsBySeed = _ => UnpavedSegmentOfLength(600) };
        var service = new RouteConstructionService(ghClient, corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.GA1, TimeSpan.FromMinutes(30), Rider);
        var result = await service.BuildRouteAsync(new RouteRequest
        {
            StartPoint = Start,
            Rider = Rider,
            Plan = new TrainingPlan { Steps = [step] },
            MaxTotalUnpavedMeters = 500,
        });

        Assert.Equal(Enumerable.Range(1, 10), ghClient.RoundTripSeeds);
        Assert.Contains(result.Warnings, w => w.Message.Contains("Streckenvarianten"));
        var expectedLat = 52.50 + 600.0 / 111_200.0;
        Assert.Equal(expectedLat, result.SurfaceSegments[0].Geometry[1].Lat, precision: 3);
    }

    [Fact]
    public async Task MaxRouteVariantAttempts_OverridesDefaultAttemptCount()
    {
        var corridors = new FakeCorridorIndex();
        // Verletzt das Limit bei jedem Versuch - ohne die Ueberschreibung wuerden 10 Versuche
        // laufen (siehe UnpavedLimitNeverSatisfied_TriesAllAttempts_UsesBestAttempt_AndWarns).
        var ghClient = new FakeGraphHopperClient { SurfaceSegmentsBySeed = _ => UnpavedSegmentOfLength(600) };
        var service = new RouteConstructionService(ghClient, corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.GA1, TimeSpan.FromMinutes(30), Rider);
        var result = await service.BuildRouteAsync(new RouteRequest
        {
            StartPoint = Start,
            Rider = Rider,
            Plan = new TrainingPlan { Steps = [step] },
            MaxTotalUnpavedMeters = 500,
            MaxRouteVariantAttempts = 3,
        });

        Assert.Equal([1, 2, 3], ghClient.RoundTripSeeds);
        Assert.Contains(result.Warnings, w => w.Message.Contains("3 probierten Streckenvarianten"));
    }

    [Fact]
    public async Task UnpavedSegmentLimitExceeded_StopsAtFirstSuccessfulSeed()
    {
        var corridors = new FakeCorridorIndex();
        var ghClient = new FakeGraphHopperClient
        {
            // Seed 1: ein einzelner 1000m-Abschnitt (verletzt Segment-Limit trotz niedrigem
            // Gesamtwert). Seed 2: kurzer 50m-Abschnitt, erfuellt beide Grenzwerte -> Abbruch dort.
            SurfaceSegmentsBySeed = seed => seed == 1 ? UnpavedSegmentOfLength(1000) : UnpavedSegmentOfLength(50),
        };
        var service = new RouteConstructionService(ghClient, corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.GA1, TimeSpan.FromMinutes(30), Rider);
        var result = await service.BuildRouteAsync(new RouteRequest
        {
            StartPoint = Start,
            Rider = Rider,
            Plan = new TrainingPlan { Steps = [step] },
            MaxUnpavedSegmentMeters = 300,
            MaxTotalUnpavedMeters = 5000, // grosszuegig, damit nur das Segment-Limit greift
        });

        Assert.Equal([1, 2], ghClient.RoundTripSeeds);
        Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("Streckenvarianten"));
    }

    [Fact]
    public async Task MaxDisruptiveJunctions_RetriesUntilJunctionCountWithinLimit()
    {
        var corridors = new FakeCorridorIndex
        {
            // Seed 1 (Distanz-Marker in der Fake-Geometrie: RoundTripDistanceRequests[0]) hat 3
            // Kreuzungen, jeder weitere Versuch nur noch 1 - erfuellt das Limit von 2.
            JunctionCountResponder = geometry => geometry[0].Lat > 52.5426187 ? 3 : 1,
        };
        // GeometryFactory kodiert den Versuch ueber die Lat-Koordinate, damit der
        // JunctionCountResponder zwischen Seed 1 und den anderen unterscheiden kann.
        var seedCounter = 0;
        var ghClient = new FakeGraphHopperClient
        {
            GeometryFactory = _ =>
            {
                seedCounter++;
                var lat = seedCounter == 1 ? Start.Lat + 1 : Start.Lat; // Seed 1 klar ueber Start.Lat
                return [new GeoPoint(lat, Start.Lon), new GeoPoint(lat, Start.Lon + 0.01)];
            },
        };
        var service = new RouteConstructionService(ghClient, corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.GA1, TimeSpan.FromMinutes(30), Rider);
        var result = await service.BuildRouteAsync(new RouteRequest
        {
            StartPoint = Start,
            Rider = Rider,
            Plan = new TrainingPlan { Steps = [step] },
            MaxDisruptiveJunctions = 2,
        });

        Assert.Equal([1, 2], ghClient.RoundTripSeeds);
        Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("Streckenvarianten"));
    }

    [Fact]
    public async Task AllowUTurnsTrue_DoesNotCheckForReversals()
    {
        var corridors = new FakeCorridorIndex { Responder = _ => MakeCorridor() };
        var ghClient = new FakeGraphHopperClient
        {
            GeometryFactory = _ =>
            [
                new GeoPoint(52.50, 13.50),
                new GeoPoint(52.51, 13.50),
                new GeoPoint(52.52, 13.50),
                new GeoPoint(52.51, 13.50),
                new GeoPoint(52.50, 13.50),
            ],
        };
        var service = new RouteConstructionService(ghClient, corridors, new PowerSpeedModel());

        var step = ZoneResolver.FromZone(TrainingZone.EB, TimeSpan.FromMinutes(5), Rider);
        var result = await service.BuildRouteAsync(new RouteRequest
        {
            StartPoint = Start,
            Rider = Rider,
            Plan = new TrainingPlan { Steps = [step] },
            AllowUTurns = true,
        });

        Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("Kehrtwende"));
    }
}
