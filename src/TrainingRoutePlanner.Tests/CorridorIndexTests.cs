using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.OsmCorridors;

namespace TrainingRoutePlanner.Tests;

public class CorridorIndexTests
{
    /// <summary>End-to-end check that CorridorIndex wires extraction, scoring and the
    /// sliding-window query together correctly against a hand-built graph (no pbf file
    /// involved) - a straight low-disruption chain between two hard-exclusion nodes, with
    /// one give-way crossing partway along it.</summary>
    [Fact]
    public void TryFindCorridor_FindsWindow_NearGivenPoint_WithinLengthAndScoreConstraints()
    {
        var graph = new RoadGraph();
        // A ~1200m straight chain along a line of longitude, roughly 100m per edge.
        var points = new GeoPoint[]
        {
            new(52.500, 13.400), // 1 - hard
            new(52.500, 13.401),
            new(52.500, 13.402),
            new(52.500, 13.403), // 4 - give-way node
            new(52.500, 13.404),
            new(52.500, 13.405),
            new(52.500, 13.406), // 7 - hard
        };
        for (int i = 0; i < points.Length; i++)
        {
            graph.SetCoordinate(i + 1, points[i]);
        }

        graph.HardNodes.Add(1);
        graph.HardNodes.Add(7);
        graph.GiveWayNodes.Add(4);

        for (int i = 1; i < points.Length; i++)
        {
            long a = i, b = i + 1;
            double length = GeoMath.HaversineMeters(points[i - 1], points[i]);
            graph.AddEdge(a, b, length, "residential");
        }

        var index = new CorridorIndex(graph);
        Assert.Equal(1, index.CorridorCount);

        var near = new GeoPoint(52.500, 13.4035); // close to the middle of the chain
        var corridor = index.TryFindCorridor(
            near,
            minLengthMeters: 300,
            maxDisruptionScore: 2.0,
            searchRadiusMeters: 500);

        Assert.NotNull(corridor);
        Assert.True(corridor!.LengthMeters >= 300);
        Assert.True(corridor.DisruptionScore <= 2.0);
        Assert.True(corridor.Geometry.Count >= 2);
        Assert.Equal(corridor.Geometry[0], corridor.Start);
        Assert.Equal(corridor.Geometry[^1], corridor.End);
    }

    [Fact]
    public void TryFindCorridor_ReturnsNull_WhenPointTooFarAway()
    {
        var graph = new RoadGraph();
        graph.SetCoordinate(1, new GeoPoint(52.500, 13.400));
        graph.SetCoordinate(2, new GeoPoint(52.500, 13.410));
        graph.HardNodes.Add(1);
        graph.HardNodes.Add(2);
        graph.AddEdge(1, 2, GeoMath.HaversineMeters(new GeoPoint(52.500, 13.400), new GeoPoint(52.500, 13.410)), "residential");

        var index = new CorridorIndex(graph);

        var farAway = new GeoPoint(53.0, 14.0);
        var corridor = index.TryFindCorridor(farAway, minLengthMeters: 100, maxDisruptionScore: 5.0, searchRadiusMeters: 1000);

        Assert.Null(corridor);
    }

    [Fact]
    public void TryFindCorridor_ReturnsNull_WhenDisruptionScoreThresholdTooStrict()
    {
        var graph = new RoadGraph();
        // 3 equal 100m edges; give-way sits right after the first hard node. A window score
        // only counts nodes strictly AFTER its left boundary, so requesting a window long
        // enough to need all 3 edges (>= 250m, more than any 2-edge subset) forces the
        // give-way node's penalty into every valid window - there is no escape window that
        // starts exactly at the give-way node and skips its own penalty.
        graph.SetCoordinate(1, new GeoPoint(52.500, 13.400));
        graph.SetCoordinate(2, new GeoPoint(52.500, 13.402));
        graph.SetCoordinate(3, new GeoPoint(52.500, 13.404));
        graph.SetCoordinate(4, new GeoPoint(52.500, 13.406));
        graph.HardNodes.Add(1);
        graph.HardNodes.Add(4);
        graph.GiveWayNodes.Add(2);
        graph.AddEdge(1, 2, 100.0, "residential");
        graph.AddEdge(2, 3, 100.0, "residential");
        graph.AddEdge(3, 4, 100.0, "residential");

        var index = new CorridorIndex(graph);

        var near = new GeoPoint(52.500, 13.403);
        var corridor = index.TryFindCorridor(near, minLengthMeters: 250, maxDisruptionScore: 0.0, searchRadiusMeters: 1000);

        Assert.Null(corridor);
    }

    /// <summary>Deckt das Bucket-Gitter aus TryFindCorridor ab (siehe CONCEPT.md
    /// Bugfix-/Performance-Abschnitt zum Spatial Index): zwei Korridore an klar getrennten
    /// Orten (~33km auseinander, also garantiert in unterschiedlichen, nicht benachbarten
    /// Gitterzellen) - der weit entfernte darf bei einem kleinen Suchradius weder faelschlich
    /// gefunden noch das Finden des nahen verhindern.</summary>
    [Fact]
    public void TryFindCorridor_WithMultipleCorridors_FindsNearOne_IgnoresFarOne()
    {
        var graph = new RoadGraph();

        // Naher Korridor, direkt am Zielpunkt.
        graph.SetCoordinate(1, new GeoPoint(52.500, 13.400));
        graph.SetCoordinate(2, new GeoPoint(52.500, 13.401));
        graph.HardNodes.Add(1);
        graph.HardNodes.Add(2);
        graph.AddEdge(1, 2, GeoMath.HaversineMeters(new GeoPoint(52.500, 13.400), new GeoPoint(52.500, 13.401)), "residential");

        // Weit entfernter Korridor (~33km noerdlich) - andere Gitterzelle als der nahe.
        graph.SetCoordinate(3, new GeoPoint(52.800, 13.400));
        graph.SetCoordinate(4, new GeoPoint(52.800, 13.401));
        graph.HardNodes.Add(3);
        graph.HardNodes.Add(4);
        graph.AddEdge(3, 4, GeoMath.HaversineMeters(new GeoPoint(52.800, 13.400), new GeoPoint(52.800, 13.401)), "residential");

        var index = new CorridorIndex(graph);
        Assert.Equal(2, index.CorridorCount);

        var near = new GeoPoint(52.500, 13.4005);
        var corridor = index.TryFindCorridor(near, minLengthMeters: 50, maxDisruptionScore: 10.0, searchRadiusMeters: 200);

        Assert.NotNull(corridor);
        Assert.True(corridor!.Start.Lat < 52.6, "Der weit entfernte Korridor haette nicht gefunden werden duerfen");
    }

    /// <summary>Ein Korridor, der nur mit einem Suchradius gefunden werden kann, der mehrere
    /// Gitterzellen ueberspannt (CorridorGridCellMeters = 500m, hier ~4000m Abstand) - prueft
    /// die radius-abhaengige cellSpan-Berechnung, nicht nur den festen 3x3-Fall.</summary>
    [Fact]
    public void TryFindCorridor_WithLargeSearchRadius_FindsCorridorAcrossMultipleGridCells()
    {
        var graph = new RoadGraph();
        // ~4000m noerdlich vom Zielpunkt (0.036 Grad Breite * 111.320 km/Grad).
        graph.SetCoordinate(1, new GeoPoint(52.536, 13.400));
        graph.SetCoordinate(2, new GeoPoint(52.5362, 13.400));
        graph.HardNodes.Add(1);
        graph.HardNodes.Add(2);
        graph.AddEdge(1, 2, GeoMath.HaversineMeters(new GeoPoint(52.536, 13.400), new GeoPoint(52.5362, 13.400)), "residential");

        var index = new CorridorIndex(graph);
        var near = new GeoPoint(52.500, 13.400);

        var tooSmallRadius = index.TryFindCorridor(near, minLengthMeters: 10, maxDisruptionScore: 10.0, searchRadiusMeters: 1000);
        Assert.Null(tooSmallRadius);

        var largeRadius = index.TryFindCorridor(near, minLengthMeters: 10, maxDisruptionScore: 10.0, searchRadiusMeters: 5000);
        Assert.NotNull(largeRadius);
    }

    [Fact]
    public void CountDisruptiveJunctionsNear_CountsDistinctHardNodesNearRoute_NotDuplicatesOrFarNodes()
    {
        var graph = new RoadGraph();
        // Zwei Ampel-Knoten (1, 3) nah an der Route, ein dritter (99) weit entfernt.
        graph.SetCoordinate(1, new GeoPoint(52.500, 13.400));
        graph.SetCoordinate(2, new GeoPoint(52.500, 13.401));
        graph.SetCoordinate(3, new GeoPoint(52.500, 13.402));
        graph.SetCoordinate(99, new GeoPoint(53.000, 14.000)); // weit weg, darf nicht mitgezaehlt werden
        graph.HardNodes.Add(1);
        graph.HardNodes.Add(3);
        graph.HardNodes.Add(99);
        graph.AddEdge(1, 2, 70, "residential");
        graph.AddEdge(2, 3, 70, "residential");

        var index = new CorridorIndex(graph);

        // Route faehrt direkt an Knoten 1 und 3 vorbei (mehrere Punkte nahe Knoten 1, damit
        // sichergestellt ist, dass er trotzdem nur EINMAL gezaehlt wird), aber nirgends nahe 99.
        var routeGeometry = new[]
        {
            new GeoPoint(52.5001, 13.3999),
            new GeoPoint(52.5000, 13.4000), // ~an Knoten 1
            new GeoPoint(52.5000, 13.4001),
            new GeoPoint(52.5000, 13.4020), // ~an Knoten 3
        };

        var count = index.CountDisruptiveJunctionsNear(routeGeometry, proximityMeters: 30);

        Assert.Equal(2, count);
    }

    [Fact]
    public void CountDisruptiveJunctionsNear_ClustersNearbyHardNodes_AsOnePhysicalIntersection()
    {
        var graph = new RoadGraph();
        // Zwei Ampel-Knoten ~10m auseinander - typisch dafuer, wie OSM eine einzelne groessere
        // Kreuzung mit je einem Signal-Knoten pro Anfahrt modelliert (siehe
        // CorridorIndex.JunctionClusterRadiusMeters). Sollen als EINE physische Kreuzung
        // zaehlen, nicht als zwei.
        graph.SetCoordinate(1, new GeoPoint(52.500, 13.400000));
        graph.SetCoordinate(2, new GeoPoint(52.500, 13.400147)); // ~10m oestlich von Knoten 1
        graph.HardNodes.Add(1);
        graph.HardNodes.Add(2);
        graph.AddEdge(1, 2, 10, "residential");

        var index = new CorridorIndex(graph);
        var routeGeometry = new[] { new GeoPoint(52.500, 13.400000) };

        var count = index.CountDisruptiveJunctionsNear(routeGeometry, proximityMeters: 30);

        Assert.Equal(1, count);
    }

    [Fact]
    public void CountDisruptiveJunctionsNear_EmptyGraph_ReturnsZero()
    {
        var graph = new RoadGraph();
        graph.SetCoordinate(1, new GeoPoint(52.500, 13.400));
        graph.SetCoordinate(2, new GeoPoint(52.500, 13.410));
        graph.AddEdge(1, 2, GeoMath.HaversineMeters(new GeoPoint(52.500, 13.400), new GeoPoint(52.500, 13.410)), "residential");

        var index = new CorridorIndex(graph);
        var count = index.CountDisruptiveJunctionsNear([new GeoPoint(52.500, 13.400)], proximityMeters: 100);

        Assert.Equal(0, count);
    }

    [Fact]
    public void GetAllJunctions_ReturnsPointsWithCorrectType()
    {
        var graph = new RoadGraph();
        graph.SetCoordinate(1, new GeoPoint(52.500, 13.400));
        graph.SetCoordinate(2, new GeoPoint(52.500, 13.401));
        graph.SetCoordinate(3, new GeoPoint(52.500, 13.402));
        graph.HardNodes.Add(1);
        graph.HardNodes.Add(3);
        graph.HardNodeTypes[1] = HardNodeType.TrafficSignal;
        graph.HardNodeTypes[3] = HardNodeType.Stop;
        graph.AddEdge(1, 2, 70, "residential");
        graph.AddEdge(2, 3, 70, "residential");

        var index = new CorridorIndex(graph);
        var junctions = index.GetAllJunctions();

        Assert.Equal(2, junctions.Count);
        Assert.Contains(junctions, j => j.Point.Equals(new GeoPoint(52.500, 13.400)) && j.Type == HardNodeType.TrafficSignal);
        Assert.Contains(junctions, j => j.Point.Equals(new GeoPoint(52.500, 13.402)) && j.Type == HardNodeType.Stop);
    }
}
