"""
Phase 0 Machbarkeits-Spike: Existieren um den Startpunkt genug lange,
unterbrechungsarme Strassenkorridore fuer strukturiertes Intervalltraining?

Nicht Teil der spaeteren C#-Produktionspipeline - Wegwerf-Validierungsskript.
Nutzt pyosmium direkt (pyrosm liess sich wegen kaputter pyrobuf-Abhaengigkeit
nicht installieren).
"""
import math
import pickle
import sys
from pathlib import Path

import networkx as nx
import osmium

PBF_PATH = "../data/sportforum-60km.osm.pbf"
GRAPH_CACHE_PATH = "../data/graph_cache.pkl"
START_LAT, START_LON = 52.5426187, 13.4763778

# Fuer Rennrad-Training relevante Strassentypen - Feldwege/Fusswege/Service
# bewusst ausgeschlossen (Belag/Relevanz), siehe CONCEPT.md Abschnitt 3.4
ROAD_HIGHWAY_TYPES = {
    "trunk", "trunk_link",
    "primary", "primary_link",
    "secondary", "secondary_link",
    "tertiary", "tertiary_link",
    "unclassified", "residential", "living_street",
}

# Score-Gewichte, siehe CONCEPT.md Abschnitt 3.4 (Platzhalter, spaeter zu kalibrieren)
HARD_EXCLUSION = math.inf
ROUNDABOUT_PENALTY = 2.0
GIVE_WAY_PENALTY = 1.0
DEFACTO_PRIORITY_PENALTY = 0.2   # unsere Strasse ist an der Kreuzung eindeutig hoeherrangig
RECHTS_VOR_LINKS_PENALTY = 1.0   # gleich-/hoeherrangige Kreuzung ohne Beschilderung -> in D
                                 # gesetzliche Rechts-vor-links-Pflicht, genauso stoerend wie
                                 # ein Give-way-Schild (muss aktiv nach rechts schauen/bremsen)

# Grobe Rangfolge deutscher Strassenklassen zur Vorfahrt-Abschaetzung an unbeschilderten
# Kreuzungen (Proxy, da "echte" Vorfahrtsregelung in OSM kaum flaechendeckend getaggt ist)
HIGHWAY_RANK = {
    "trunk": 6, "trunk_link": 6,
    "primary": 5, "primary_link": 5,
    "secondary": 4, "secondary_link": 4,
    "tertiary": 3, "tertiary_link": 3,
    "unclassified": 2,
    "residential": 1,
    "living_street": 0,
}

SCORE_THRESHOLDS = {
    "VO2max/Sprint (Schwelle ~1)": 1.0,
    "SB (Schwelle ~1.5)": 1.5,
    "EB (Schwelle ~3)": 3.0,
}
MIN_LENGTHS_M = [1000, 2000, 3000]


def haversine_m(lat1, lon1, lat2, lon2):
    r = 6_371_000
    p1, p2 = math.radians(lat1), math.radians(lat2)
    dphi = math.radians(lat2 - lat1)
    dlambda = math.radians(lon2 - lon1)
    a = math.sin(dphi / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(dlambda / 2) ** 2
    return 2 * r * math.asin(math.sqrt(a))


class NetworkHandler(osmium.SimpleHandler):
    def __init__(self):
        super().__init__()
        self.graph = nx.Graph()
        self.coords = {}
        self.hard_nodes = set()
        self.give_way_nodes = set()
        self.roundabout_nodes = set()
        self.ways_seen = 0

    def node(self, n):
        hw = n.tags.get("highway")
        if hw in ("traffic_signals", "stop"):
            self.hard_nodes.add(n.id)
        elif hw == "give_way":
            self.give_way_nodes.add(n.id)

    def way(self, w):
        hw = w.tags.get("highway")
        if hw not in ROAD_HIGHWAY_TYPES:
            return
        self.ways_seen += 1
        junction = w.tags.get("junction")
        surface = w.tags.get("surface")

        prev = None
        for nref in w.nodes:
            if not nref.location.valid():
                prev = None
                continue
            cur_id = nref.ref
            cur_lat, cur_lon = nref.location.lat, nref.location.lon
            self.coords[cur_id] = (cur_lat, cur_lon)
            if prev is not None:
                prev_id, prev_lat, prev_lon = prev
                if not self.graph.has_edge(prev_id, cur_id):
                    length = haversine_m(prev_lat, prev_lon, cur_lat, cur_lon)
                    self.graph.add_edge(
                        prev_id, cur_id, length=length, highway=hw, surface=surface
                    )
                if junction == "roundabout":
                    self.roundabout_nodes.add(prev_id)
                    self.roundabout_nodes.add(cur_id)
            prev = (cur_id, cur_lat, cur_lon)


def bearing_deg(lat1, lon1, lat2, lon2):
    phi1, phi2 = math.radians(lat1), math.radians(lat2)
    dlambda = math.radians(lon2 - lon1)
    x = math.sin(dlambda) * math.cos(phi2)
    y = math.cos(phi1) * math.sin(phi2) - math.sin(phi1) * math.cos(phi2) * math.cos(dlambda)
    return math.degrees(math.atan2(x, y)) % 360


def angle_diff(a, b):
    d = abs(a - b) % 360
    return min(d, 360 - d)


def pick_straightest(coords, prev_id, cur_id, candidates):
    """An weichen Kreuzungen die Richtung waehlen, die am ehesten geradeaus weiterfuehrt -
    naehert an, wie ein Radfahrer der Strasse folgen wuerde, statt zufaellig abzubiegen."""
    if len(candidates) == 1:
        return candidates[0]
    plat, plon = coords[prev_id]
    clat, clon = coords[cur_id]
    incoming = bearing_deg(plat, plon, clat, clon)
    best = None
    for cand in candidates:
        nlat, nlon = coords[cand]
        outgoing = bearing_deg(clat, clon, nlat, nlon)
        diff = angle_diff(incoming, outgoing)
        if best is None or diff < best[0]:
            best = (diff, cand)
    return best[1]


def static_tag_score(node, handler: NetworkHandler):
    """Score, der NICHT von der Durchfahrtsrichtung abhaengt (Ampel/Stopp/Kreisverkehr/
    Give-way sind feste OSM-Tags). None = unbeschildert, haengt von Fahrtrichtung +
    Strassenklassen ab (siehe junction_penalty)."""
    if node in handler.hard_nodes:
        return HARD_EXCLUSION
    if node in handler.roundabout_nodes:
        return ROUNDABOUT_PENALTY
    if node in handler.give_way_nodes:
        return GIVE_WAY_PENALTY
    return None


def junction_penalty(g: nx.Graph, prev, cur, nxt):
    """Score-Beitrag einer unbeschilderten Kreuzung: vergleicht die Strassenklasse
    unserer Fahrtrichtung mit den kreuzenden Strassen. Nur wenn wir eindeutig
    hoeherrangig sind, gilt faktische Vorfahrt - sonst greift (in Deutschland) die
    gesetzliche Rechts-vor-links-Regel, die genauso zum Abbremsen/Schauen zwingt wie
    ein Give-way-Schild. Siehe CONCEPT.md Abschnitt 3.4/4.1."""
    if g.degree(cur) < 3:
        return 0.0
    incoming_rank = HIGHWAY_RANK.get(g[prev][cur].get("highway"), 0)
    crossing_ranks = [
        HIGHWAY_RANK.get(g[cur][nbr].get("highway"), 0)
        for nbr in g.neighbors(cur)
        if nbr != prev and nbr != nxt
    ]
    if not crossing_ranks:
        return 0.0
    if incoming_rank > max(crossing_ranks):
        return DEFACTO_PRIORITY_PENALTY
    return RECHTS_VOR_LINKS_PENALTY


def extract_corridors(g: nx.Graph, handler: NetworkHandler, coords: dict) -> list:
    """Zerlegt den Graphen in maximale Ketten, die NUR an harten Ausschluss-Knoten
    (Ampel/Stopp) oder Sackgassen enden - laeuft durch alle weichen Kreuzungen
    hindurch (Kreisverkehr, Give-way, unmarkierte Kreuzung), siehe CONCEPT.md 4.1."""
    visited_edges = set()
    corridors = []

    def is_hard(node):
        return static_tag_score(node, handler) == HARD_EXCLUSION

    for start_node in list(g.nodes):
        if not is_hard(start_node):
            continue  # nur von harten Ausschluss-Knoten aus starten
        for neighbor in g.neighbors(start_node):
            edge_key = frozenset((start_node, neighbor))
            if edge_key in visited_edges:
                continue

            path_nodes = [start_node, neighbor]
            visited_edges.add(edge_key)
            prev, cur = start_node, neighbor
            while not is_hard(cur):
                candidates = [
                    n for n in g.neighbors(cur)
                    if n != prev and frozenset((cur, n)) not in visited_edges
                ]
                if not candidates:
                    break
                nxt = pick_straightest(coords, prev, cur, candidates)
                visited_edges.add(frozenset((cur, nxt)))
                path_nodes.append(nxt)
                prev, cur = cur, nxt

            if len(path_nodes) >= 2:
                corridors.append(path_nodes)
    return corridors


def corridor_profile(g: nx.Graph, handler: NetworkHandler, path_nodes: list):
    dist = [0.0]
    score = [0.0]
    n = len(path_nodes)
    for i in range(1, n):
        a, b = path_nodes[i - 1], path_nodes[i]
        edge_len = g[a][b]["length"]
        dist.append(dist[-1] + edge_len)

        tag_score = static_tag_score(b, handler)
        if tag_score == HARD_EXCLUSION:
            node_score = 0.0  # Endpunkt ist Korridorgrenze, keine Durchquerung
        elif tag_score is not None:
            node_score = tag_score
        elif i + 1 < n:
            node_score = junction_penalty(g, a, b, path_nodes[i + 1])
        else:
            node_score = 0.0  # Korridorende (Sackgasse), keine weitere Kreuzung
        score.append(score[-1] + node_score)
    return dist, score


def best_window(dist: list, score: list, min_len_m: float):
    """Guenstigstes Fenster mit Laenge >= min_len_m. `left` wird nur verschoben,
    wenn das resultierende Fenster IMMER NOCH gueltig (>= min_len_m) waere - sonst
    ueberspringt man bei groben Kantenlaengen ein gueltiges Fenster ersatzlos."""
    n = len(dist)
    best = None
    left = 0
    for right in range(n):
        while left + 1 <= right and dist[right] - dist[left + 1] >= min_len_m:
            left += 1
        if dist[right] - dist[left] >= min_len_m:
            window_score = score[right] - score[left]
            if best is None or window_score < best[0]:
                best = (window_score, left, right)
    return best


def load_handler() -> NetworkHandler:
    cache_file = Path(GRAPH_CACHE_PATH)
    if cache_file.exists():
        print(f"Lade zwischengespeicherten Graphen aus {GRAPH_CACHE_PATH} ...")
        with cache_file.open("rb") as f:
            return pickle.load(f)
    print(f"Lade OSM-Netz aus {PBF_PATH} ...")
    handler = NetworkHandler()
    handler.apply_file(PBF_PATH, locations=True)
    with cache_file.open("wb") as f:
        pickle.dump(handler, f)
    return handler


def main():
    handler = load_handler()
    g = handler.graph
    print(f"Relevante Wege verarbeitet: {handler.ways_seen}")
    print(f"Graph: {g.number_of_nodes()} Knoten, {g.number_of_edges()} Kanten")
    print(f"Harte Ausschluss-Knoten (Ampel/Stopp): {len(handler.hard_nodes)}")
    print(f"Kreisverkehr-Knoten: {len(handler.roundabout_nodes)}")
    print(f"Give-way-Knoten: {len(handler.give_way_nodes)}")

    corridors = extract_corridors(g, handler, handler.coords)
    print(f"Gefundene Korridore (Ketten zwischen Kreuzungen/Ausschluss-Knoten): {len(corridors)}")

    results = {name: {ml: 0 for ml in MIN_LENGTHS_M} for name in SCORE_THRESHOLDS}
    longest_ok = {name: {ml: 0.0 for ml in MIN_LENGTHS_M} for name in SCORE_THRESHOLDS}

    for path_nodes in corridors:
        dist, score = corridor_profile(g, handler, path_nodes)
        total_len = dist[-1]
        if total_len < min(MIN_LENGTHS_M):
            continue
        for min_len in MIN_LENGTHS_M:
            if total_len < min_len:
                continue
            window = best_window(dist, score, min_len)
            if window is None:
                continue
            window_score = window[0]
            for zone_name, threshold in SCORE_THRESHOLDS.items():
                if window_score <= threshold:
                    results[zone_name][min_len] += 1
                    longest_ok[zone_name][min_len] = max(longest_ok[zone_name][min_len], total_len)

    print("\n=== Ergebnis: Anzahl Korridore, die Score-Schwelle UND Mindestlaenge erfuellen ===")
    for zone_name in SCORE_THRESHOLDS:
        print(f"\n{zone_name}:")
        for min_len in MIN_LENGTHS_M:
            count = results[zone_name][min_len]
            longest = longest_ok[zone_name][min_len]
            print(f"  >= {min_len} m: {count} Korridore (laengster Treffer: {longest:.0f} m)")

    # --- Diagnose: Verteilung von Laenge und Score/km ueber alle Korridore ---
    lengths = []
    score_per_km = []
    for path_nodes in corridors:
        dist, score = corridor_profile(g, handler, path_nodes)
        total_len = dist[-1]
        if total_len < 200:
            continue
        lengths.append(total_len)
        score_per_km.append(score[-1] / (total_len / 1000.0))

    lengths.sort()
    score_per_km.sort()
    n = len(lengths)
    print(f"\n=== Diagnose ueber {n} Korridore (>=200m) ===")
    if n:
        def pct(arr, p):
            return arr[min(len(arr) - 1, int(len(arr) * p))]
        print(f"Laenge: min={lengths[0]:.0f}m, p50={pct(lengths,0.5):.0f}m, "
              f"p90={pct(lengths,0.9):.0f}m, max={lengths[-1]:.0f}m")
        print(f"Score/km: min={score_per_km[0]:.2f}, p50={pct(score_per_km,0.5):.2f}, "
              f"p90={pct(score_per_km,0.9):.2f}, max={score_per_km[-1]:.2f}")
        long_corridors = sum(1 for l in lengths if l >= 1000)
        print(f"Korridore >= 1000m (unabhaengig vom Score): {long_corridors}")


if __name__ == "__main__":
    sys.exit(main())
