using System.Linq;
using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.OsmCorridors;

/// <summary>Public entry point for the corridor pre-computation and query pipeline
/// (CONCEPT.md section 4.1). Build once per region with <see cref="Load"/> (or, for tests,
/// via the internal graph-based constructor against a hand-built <see cref="RoadGraph"/> -
/// see CONCEPT.md testing requirements), then answer many cheap
/// <see cref="TryFindCorridor"/> queries against it.</summary>
public sealed class CorridorIndex : ICorridorIndex
{
    private readonly RoadGraph _graph;
    private readonly List<CorridorProfile> _corridors;
    private readonly Dictionary<(int, int), List<int>> _corridorGrid;
    private readonly Dictionary<(int, int), List<long>> _hardNodeGrid;
    private readonly Dictionary<long, long> _hardNodeClusterRoot;

    // Bucket-Gitter ueber Geometriepunkte ALLER Korridore fuer TryFindCorridor - ersetzt den
    // frueheren vollstaendigen linearen Scan (siehe CONCEPT.md Bugfix-/Performance-Abschnitt zum
    // Spatial Index, ehemals Abschnitt 7 "Offene Punkte"). Bewusst ueber GEOMETRIEPUNKTE statt
    // ueber die Gesamt-Bounding-Box eines Korridors indiziert: Korridore variieren stark in der
    // Laenge (von kurzen Reststuecken bis zu 55km, siehe CONCEPT.md 6.1) - eine Gesamt-Bbox waere
    // bei den langen Ausreissern viel zu grob und wuerde kaum Kandidaten ausschliessen. Anders als
    // beim Ampeln-Gitter (siehe HardNodeGridCellMeters) ist der Suchradius hier NICHT konstant
    // klein (800m Start, eskaliert bis zum anfahrtszeit-basierten Limit, i.d.R. wenige km) -
    // TryFindCorridor berechnet die Anzahl zu scannender Nachbarzellen daher radius-abhaengig
    // statt eines festen 3x3-Scans.
    private const double CorridorGridCellMeters = 500.0;

    // Grobes Gitter ueber die Ampel-/Stopp-Knoten (RoadGraph.HardNodes) fuer
    // CountDisruptiveJunctionsNear - ein einfaches Bucket-Gitter extra fuer diese eine
    // Abfrageart (die Korridorsuche hat seit dem Spatial-Index-Umbau ihr eigenes, siehe
    // _corridorGrid/CorridorGridCellMeters). Zellgroesse deutlich groesser als jede sinnvolle
    // proximityMeters-Anfrage, damit ein 3x3-Nachbarzellen-Scan garantiert alle Kandidaten
    // findet, auch nahe an Zellgrenzen.
    private const double HardNodeGridCellMeters = 200.0;

    // OSM modelliert eine einzelne reale Ampel-/Stopp-Kreuzung oft mit MEHREREN Knoten (ein
    // Knoten je Anfahrt/Fahrspur, besonders bei groesseren Berliner Kreuzungen), die laut
    // Recherche typischerweise wenige Meter bis ~20m auseinanderliegen. Live gemeldeter Bug:
    // eine reale Route zeigte "57 Ampel-/Stopp-Kreuzungen" an, weil CountDisruptiveJunctionsNear
    // urspruenglich nach roher OSM-Node-ID statt nach physischer Kreuzung deduplizierte - eine
    // grosse Kreuzung mit 3-4 Signal-Knoten zaehlte entsprechend 3-4 mal. 20m als Cluster-Radius
    // ist ein bewusster Kompromiss: gross genug, um die ueblichen Mehrfach-Knoten EINER Kreuzung
    // zusammenzufassen, aber (ausser in sehr dicht bebauten Innenstadtbloecken) klein genug, um
    // zwei tatsaechlich unterschiedliche, nahe beieinanderliegende Kreuzungen nicht faelschlich
    // zu verschmelzen.
    private const double JunctionClusterRadiusMeters = 20.0;

    internal CorridorIndex(RoadGraph graph)
    {
        _graph = graph;
        var rawCorridors = CorridorExtractor.ExtractCorridors(graph);
        _corridors = new List<CorridorProfile>(rawCorridors.Count);

        foreach (var pathNodes in rawCorridors)
        {
            _corridors.Add(CorridorProfileBuilder.Build(graph, pathNodes));
        }

        _corridorGrid = BuildCorridorGrid(graph, _corridors);
        _hardNodeGrid = BuildHardNodeGrid(graph);
        _hardNodeClusterRoot = BuildHardNodeClusters(graph, _hardNodeGrid);
    }

    /// <summary>Siehe <see cref="ICorridorIndex.CountDisruptiveJunctionsNear"/>.</summary>
    public int CountDisruptiveJunctionsNear(IReadOnlyList<GeoPoint> routeGeometry, double proximityMeters)
    {
        var found = new HashSet<long>();
        foreach (var point in routeGeometry)
        {
            var (cellX, cellY) = HardNodeGridCell(point);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!_hardNodeGrid.TryGetValue((cellX + dx, cellY + dy), out var candidates))
                        continue;

                    foreach (var nodeId in candidates)
                    {
                        if (found.Contains(nodeId))
                            continue;
                        if (GeoMath.HaversineMeters(point, _graph.Coordinates[nodeId]) <= proximityMeters)
                            found.Add(nodeId);
                    }
                }
            }
        }

        // Auf physische Kreuzungen statt roher OSM-Node-ID deduplizieren - siehe
        // JunctionClusterRadiusMeters.
        var clusters = new HashSet<long>();
        foreach (var nodeId in found)
            clusters.Add(_hardNodeClusterRoot[nodeId]);
        return clusters.Count;
    }

    /// <summary>Siehe <see cref="ICorridorIndex.GetAllJunctions"/>.</summary>
    public IReadOnlyList<Junction> GetAllJunctions()
    {
        var result = new List<Junction>(_graph.HardNodeTypes.Count);
        foreach (var (nodeId, type) in _graph.HardNodeTypes)
        {
            if (_graph.Coordinates.TryGetValue(nodeId, out var point))
                result.Add(new Junction(point, type));
        }
        return result;
    }

    // Jeder Korridor traegt seinen Index EINMAL pro tatsaechlich beruehrter Zelle ein (lokales
    // HashSet pro Korridor vermeidet Mehrfacheintraege in derselben Zelle bei dicht
    // aufeinanderfolgenden Geometriepunkten) - haelt die Kandidatenlisten pro Zelle kompakt.
    private static Dictionary<(int, int), List<int>> BuildCorridorGrid(RoadGraph graph, List<CorridorProfile> corridors)
    {
        var grid = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < corridors.Count; i++)
        {
            var visitedCells = new HashSet<(int, int)>();
            foreach (var nodeId in corridors[i].PathNodes)
            {
                if (!graph.Coordinates.TryGetValue(nodeId, out var point))
                    continue;

                var cell = CorridorGridCell(point);
                if (!visitedCells.Add(cell))
                    continue;

                if (!grid.TryGetValue(cell, out var list))
                {
                    list = new List<int>();
                    grid[cell] = list;
                }
                list.Add(i);
            }
        }
        return grid;
    }

    private static (int, int) CorridorGridCell(GeoPoint p) => (
        (int)Math.Floor(p.Lat * 111_320.0 / CorridorGridCellMeters),
        (int)Math.Floor(p.Lon * 111_320.0 * Math.Cos(GeoMath.DegreesToRadians(p.Lat)) / CorridorGridCellMeters));

    private static Dictionary<(int, int), List<long>> BuildHardNodeGrid(RoadGraph graph)
    {
        var grid = new Dictionary<(int, int), List<long>>();
        foreach (var nodeId in graph.HardNodes)
        {
            if (!graph.Coordinates.TryGetValue(nodeId, out var point))
                continue;

            var cell = HardNodeGridCell(point);
            if (!grid.TryGetValue(cell, out var list))
            {
                list = new List<long>();
                grid[cell] = list;
            }
            list.Add(nodeId);
        }
        return grid;
    }

    // Union-Find ueber alle Ampel-/Stopp-Knoten: zwei Knoten landen im selben Cluster, sobald
    // eine Kette von Paaren mit je hoechstens JunctionClusterRadiusMeters Abstand sie verbindet
    // (nicht nur direkte Paare - bei einer Kreuzung mit 3+ Signal-Knoten in einer Reihe reicht
    // das, um trotzdem alle in EINEN Cluster zu bekommen). Nutzt dasselbe Bucket-Gitter wie
    // CountDisruptiveJunctionsNear fuer die Kandidatensuche - die Gitterzelle (200m) ist deutlich
    // groesser als der Cluster-Radius (20m), ein 3x3-Nachbarzellen-Scan findet also garantiert
    // alle Kandidaten.
    private static Dictionary<long, long> BuildHardNodeClusters(RoadGraph graph, Dictionary<(int, int), List<long>> grid)
    {
        var parent = new Dictionary<long, long>();
        foreach (var nodeId in graph.HardNodes)
            parent[nodeId] = nodeId;

        long Find(long x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(long a, long b)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (rootA != rootB)
                parent[rootA] = rootB;
        }

        foreach (var nodeId in graph.HardNodes)
        {
            if (!graph.Coordinates.TryGetValue(nodeId, out var point))
                continue;

            var (cellX, cellY) = HardNodeGridCell(point);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!grid.TryGetValue((cellX + dx, cellY + dy), out var candidates))
                        continue;

                    foreach (var otherNodeId in candidates)
                    {
                        if (otherNodeId <= nodeId)
                            continue; // jedes Paar nur einmal betrachten
                        if (GeoMath.HaversineMeters(point, graph.Coordinates[otherNodeId]) <= JunctionClusterRadiusMeters)
                            Union(nodeId, otherNodeId);
                    }
                }
            }
        }

        return graph.HardNodes.ToDictionary(nodeId => nodeId, Find);
    }

    private static (int, int) HardNodeGridCell(GeoPoint p) => (
        (int)Math.Floor(p.Lat * 111_320.0 / HardNodeGridCellMeters),
        (int)Math.Floor(p.Lon * 111_320.0 * Math.Cos(GeoMath.DegreesToRadians(p.Lat)) / HardNodeGridCellMeters));

    /// <summary>Builds the full graph from a .osm.pbf file and extracts + scores all
    /// corridors once. Expensive (pbf parse + graph walk) - meant to run once per region
    /// and be cached/reused, not per request (see CONCEPT.md 4.1).</summary>
    public static CorridorIndex Load(string pbfPath)
    {
        var graph = PbfGraphBuilder.Build(pbfPath);
        return new CorridorIndex(graph);
    }

    /// <summary>Number of extracted corridors (chains between hard-exclusion nodes / dead
    /// ends). Exposed mainly for diagnostics/sanity-checking against CONCEPT.md 6.1's
    /// reference counts.</summary>
    public int CorridorCount => _corridors.Count;

    /// <summary>Finds a corridor sub-window of at least <paramref name="minLengthMeters"/>
    /// whose disruption score is at most <paramref name="maxDisruptionScore"/> and whose
    /// geometry passes within <paramref name="searchRadiusMeters"/> of
    /// <paramref name="near"/>.
    ///
    /// Scans only the corridors whose geometry touches a grid cell within
    /// <paramref name="searchRadiusMeters"/> of <paramref name="near"/> (siehe _corridorGrid) -
    /// vormals ein voller linearer Scan ueber ALLE Korridore, siehe CONCEPT.md
    /// Bugfix-/Performance-Abschnitt zum Spatial Index. Kandidaten werden ueber ein SortedSet
    /// gesammelt (nicht HashSet), damit bei Distanz-Gleichstand exakt derselbe Gewinner wie beim
    /// alten linearen Scan (aufsteigende Index-Reihenfolge) gewaehlt wird - reine
    /// Performance-Aenderung, keine Verhaltensaenderung.</summary>
    public Corridor? TryFindCorridor(
        GeoPoint near,
        double minLengthMeters,
        double maxDisruptionScore,
        double searchRadiusMeters)
    {
        var candidates = FindCandidateCorridors(near, searchRadiusMeters);

        Corridor? best = null;
        double bestDistanceMeters = double.MaxValue;

        foreach (var i in candidates)
        {
            var profile = _corridors[i];
            if (profile.TotalLengthMeters < minLengthMeters)
            {
                continue;
            }

            var window = SlidingWindow.BestWindow(profile.Dist, profile.Score, minLengthMeters);
            if (window is null || window.Value.Score > maxDisruptionScore)
            {
                continue;
            }

            var geometry = BuildGeometry(profile, window.Value.Left, window.Value.Right);
            double distanceMeters = MinDistanceToPolyline(near, geometry);
            if (distanceMeters > searchRadiusMeters)
            {
                continue;
            }

            if (distanceMeters < bestDistanceMeters)
            {
                bestDistanceMeters = distanceMeters;
                best = new Corridor
                {
                    Start = geometry[0],
                    End = geometry[^1],
                    LengthMeters = profile.Dist[window.Value.Right] - profile.Dist[window.Value.Left],
                    DisruptionScore = window.Value.Score,
                    Geometry = geometry,
                };
            }
        }

        return best;
    }

    // "+1" Zelle Sicherheitsmarge wie beim Ampeln-Gitter (siehe HardNodeGridCellMeters-
    // Kommentar) - garantiert, dass auch Korridor-Punkte nahe einer Zellgrenze gefunden werden.
    // Anders als dort ist der Radius hier nicht klein/konstant, daher radius-abhaengige
    // cellSpan statt eines festen 3x3-Scans.
    private SortedSet<int> FindCandidateCorridors(GeoPoint near, double searchRadiusMeters)
    {
        var (centerX, centerY) = CorridorGridCell(near);
        var cellSpan = (int)Math.Ceiling(searchRadiusMeters / CorridorGridCellMeters) + 1;

        var candidates = new SortedSet<int>();
        for (var dx = -cellSpan; dx <= cellSpan; dx++)
        {
            for (var dy = -cellSpan; dy <= cellSpan; dy++)
            {
                if (_corridorGrid.TryGetValue((centerX + dx, centerY + dy), out var list))
                {
                    foreach (var i in list)
                        candidates.Add(i);
                }
            }
        }
        return candidates;
    }

    private List<GeoPoint> BuildGeometry(CorridorProfile profile, int left, int right)
    {
        var geometry = new List<GeoPoint>(right - left + 1);
        for (int i = left; i <= right; i++)
        {
            geometry.Add(_graph.Coordinates[profile.PathNodes[i]]);
        }

        return geometry;
    }

    private static double MinDistanceToPolyline(GeoPoint p, IReadOnlyList<GeoPoint> geometry)
    {
        if (geometry.Count == 1)
        {
            return GeoMath.HaversineMeters(p, geometry[0]);
        }

        double best = double.MaxValue;
        for (int i = 1; i < geometry.Count; i++)
        {
            double d = GeoMath.DistanceMetersToSegment(p, geometry[i - 1], geometry[i]);
            if (d < best)
            {
                best = d;
            }
        }

        return best;
    }
}
