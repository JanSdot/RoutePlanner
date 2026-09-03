import { useEffect, useRef } from "react";
import { Map as MapLibreMap, Marker, LngLatBounds, Popup } from "maplibre-gl";
import type { StyleSpecification, GeoJSONSource, ExpressionSpecification } from "maplibre-gl";
import "maplibre-gl/dist/maplibre-gl.css";
import type { FeatureCollection, LineString, Point } from "geojson";
import type { GeoPoint, RouteSegment, SurfaceSegment, BlockedArea, ConstructionClosure, Junction, SegmentLock } from "../types";
import { requestJunctions } from "../api";

// Fester Radius fuer per Klick gesperrte Bereiche - kein UI-Element zum Anpassen, um die
// Interaktion einfach zu halten (siehe CONCEPT.md Abschnitt 6.18). Exportiert, da App.tsx
// denselben Radius fuer die neuen persistierten Sperr-Arten (dauerhaft/Verein, Stufe 3)
// verwendet - ein Nutzer soll nicht ueberlegen muessen, warum sich der Radius je nach Sperr-Art
// unterscheidet.
export const DEFAULT_BLOCK_RADIUS_METERS = 40;

const OSM_RASTER_STYLE: StyleSpecification = {
  version: 8,
  sources: {
    osm: {
      type: "raster",
      tiles: ["https://tile.openstreetmap.org/{z}/{x}/{y}.png"],
      tileSize: 256,
      attribution: "&copy; OpenStreetMap contributors",
    },
  },
  layers: [{ id: "osm", type: "raster", source: "osm" }],
};

// Feste Palette statt zufaelliger Farben, damit derselbe Label (z.B. "Work") bei jeder
// Neuberechnung dieselbe Farbe bekommt.
export const SEGMENT_COLOR_PALETTE = ["#ea580c", "#7c3aed", "#0d9488", "#db2777", "#65a30d", "#0891b2"];
export const SEGMENT_FALLBACK_COLOR = "#f59e0b";

export function colorForSegmentLabel(label: string, allLabels: string[]): string {
  const uniqueLabels = [...new Set(allLabels)];
  const index = uniqueLabels.indexOf(label);
  return index === -1 ? SEGMENT_FALLBACK_COLOR : SEGMENT_COLOR_PALETTE[index % SEGMENT_COLOR_PALETTE.length];
}

// GraphHopper/OSM "surface"-Werte, die auf einen fuer Rennrad/Standard-Reifen spuerbar
// unangenehmen Untergrund hindeuten. Bewusst eine Denyliste statt einer Erlaubnisliste: viele
// asphaltierte Strassen tragen in OSM gar kein surface-Tag (GraphHopper liefert dann "missing"),
// waehrend surface=unpaved/gravel/... fast immer explizit gesetzt wird, gerade WEIL es die
// Ausnahme ist. Eine Erlaubnisliste wuerde also die meisten echten Asphaltstrecken faelschlich
// markieren.
const UNPAVED_SURFACES = new Set([
  "unpaved", "gravel", "fine_gravel", "dirt", "ground", "sand", "mud", "grass", "grass_paver",
  "pebblestone", "cobblestone", "sett", "unhewn_cobblestone", "compacted", "woodchips", "rock",
]);
export const SURFACE_WARNING_COLOR = "#dc2626";

// GraphHopper/OSM "smoothness"-Werte fuer einen an sich befestigten, aber spuerbar rauen Belag
// (z.B. rissiger alter Asphalt) - siehe SurfaceClassifier.IsBadSmoothness (Backend), muss damit
// abgeglichen bleiben. Bewusst als EIGENER Layer/Farbe statt zusammen mit UNPAVED_SURFACES
// dargestellt: rauer Belag ist etwas anderes als unbefestigter Untergrund (siehe CONCEPT.md
// Bugfix-Abschnitt zu ueberhoehten Warnungs-Zahlen).
const BAD_SMOOTHNESS_VALUES = new Set(["bad", "very_bad", "horrible", "very_horrible", "impassable"]);
// Braun statt Rot (SURFACE_WARNING_COLOR) - klar unterscheidbar, da rauer Belag fachlich etwas
// anderes ist als unbefestigter Untergrund.
export const ROUGH_SURFACE_WARNING_COLOR = "#92400e";

// Baustellen-Sperrungen-Layer (VIZ Berlin, siehe CONCEPT.md 6.27) - orange statt rot, um sich
// klar vom manuell gesperrten BlockedArea-Kreis (#dc2626) UND der Untergrund-Warnung
// (SURFACE_WARNING_COLOR) zu unterscheiden.
export const CONSTRUCTION_CLOSURE_COLOR = "#ea580c";

// Persistierte Sperr-Bereiche (CONCEPT.md Phase-4-Backlog "Mehrbenutzerfähigkeit/Auth/Vereine",
// Stufe 3) - eigene Farben, klar unterscheidbar von der temporären (roten) BlockedArea sowie
// voneinander (persönlich vs. Verein).
export const PERSONAL_LOCK_COLOR = "#7c3aed";
export const CLUB_LOCK_COLOR = "#0d9488";

function segmentColorExpression(labels: string[]): ExpressionSpecification | string {
  const uniqueLabels = [...new Set(labels)];
  // MapLibre's "match" requires at least one case/output pair - with zero labels (e.g. before
  // any route is computed) that produces an invalid expression, so fall back to a plain color.
  if (uniqueLabels.length === 0) return SEGMENT_FALLBACK_COLOR;
  const stops = uniqueLabels.flatMap((label, i) => [label, SEGMENT_COLOR_PALETTE[i % SEGMENT_COLOR_PALETTE.length]]);
  return ["match", ["get", "label"], ...stops, SEGMENT_FALLBACK_COLOR] as unknown as ExpressionSpecification;
}

interface MapViewProps {
  startPoint: GeoPoint | null;
  onStartPointChange: (point: GeoPoint) => void;
  routeGeometry: GeoPoint[] | null;
  routeSegments: RouteSegment[] | null;
  surfaceSegments: SurfaceSegment[] | null;
  // Wie surfaceSegments, aber ueber das "smoothness"-Tag - eigener Layer/Farbe
  // (ROUGH_SURFACE_WARNING_COLOR), siehe CONCEPT.md Bugfix-Abschnitt zu ueberhoehten
  // Warnungs-Zahlen.
  smoothnessSegments: SurfaceSegment[] | null;
  blockedAreas: BlockedArea[];
  onAddBlockedArea: (area: BlockedArea) => void;
  requiredPoints: GeoPoint[];
  onAddRequiredPoint: (point: GeoPoint) => void;
  // Automatisch erkannte Baustellen-Sperrungen (VIZ Berlin, siehe CONCEPT.md Abschnitt 6.27) -
  // rein anzeigend, anders als blockedAreas/requiredPoints gibt es hier keinen
  // Karten-Klick-Handler zum Hinzufügen (die Daten kommen aus dem Feed, nicht vom Nutzer).
  constructionClosures: ConstructionClosure[];
  ignoredClosureIds: Set<string>;
  showConstructionClosures: boolean;
  showJunctions: boolean;
  // Persistierte Sperr-Bereiche (persoenlich + freigegebene Vereins-Sperren), siehe
  // CONCEPT.md Phase-4-Backlog "Mehrbenutzerfähigkeit/Auth/Vereine", Stufe 3. Immer sichtbar
  // (kein Ein-/Ausblenden-Toggle wie bei Ampeln/Baustellen), da es vergleichsweise wenige sind
  // und der Nutzer aktiv wissen sollte, wo er dauerhaft nicht mehr hinrouten kann.
  segmentLocks: SegmentLock[];
  // Steuert, ob der Kartenklick-Popup die dritte Option ("Für Verein vorschlagen") ueberhaupt
  // anbietet - nur approved Mitglieder duerfen das.
  isApprovedClubMember: boolean;
  onCreatePersonalLock: (point: GeoPoint) => Promise<void>;
  onProposeClubLock: (point: GeoPoint) => Promise<void>;
  // Fuer /junctions - MapView wird erst nach erfolgreichem Login gerendert (siehe App.tsx),
  // ein gueltiges Token ist an dieser Stelle daher immer vorhanden.
  authToken: string;
}

export function MapView({
  startPoint,
  onStartPointChange,
  routeGeometry,
  routeSegments,
  surfaceSegments,
  smoothnessSegments,
  blockedAreas,
  onAddBlockedArea,
  requiredPoints,
  onAddRequiredPoint,
  constructionClosures,
  ignoredClosureIds,
  showConstructionClosures,
  showJunctions,
  segmentLocks,
  isApprovedClubMember,
  onCreatePersonalLock,
  onProposeClubLock,
  authToken,
}: MapViewProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<MapLibreMap | null>(null);
  const startMarkerRef = useRef<Marker | null>(null);
  const onStartPointChangeRef = useRef(onStartPointChange);
  onStartPointChangeRef.current = onStartPointChange;
  const onAddBlockedAreaRef = useRef(onAddBlockedArea);
  onAddBlockedAreaRef.current = onAddBlockedArea;
  const onAddRequiredPointRef = useRef(onAddRequiredPoint);
  onAddRequiredPointRef.current = onAddRequiredPoint;
  const showJunctionsRef = useRef(showJunctions);
  showJunctionsRef.current = showJunctions;
  const showConstructionClosuresRef = useRef(showConstructionClosures);
  showConstructionClosuresRef.current = showConstructionClosures;
  const isApprovedClubMemberRef = useRef(isApprovedClubMember);
  isApprovedClubMemberRef.current = isApprovedClubMember;
  const onCreatePersonalLockRef = useRef(onCreatePersonalLock);
  onCreatePersonalLockRef.current = onCreatePersonalLock;
  const onProposeClubLockRef = useRef(onProposeClubLock);
  onProposeClubLockRef.current = onProposeClubLock;
  // MapLibre's "load" event fires exactly once per map instance. isStyleLoaded() can also
  // transiently report false during unrelated tile activity long after the initial load, so
  // neither is safe to re-check on every route update - track it ourselves instead.
  const styleReadyRef = useRef(false);

  useEffect(() => {
    if (!containerRef.current) return;

    const map = new MapLibreMap({
      container: containerRef.current,
      style: OSM_RASTER_STYLE,
      center: [13.4763778, 52.5426187], // Sportforum Berlin
      zoom: 11,
    });
    mapRef.current = map;
    styleReadyRef.current = false;
    map.once("load", () => {
      styleReadyRef.current = true;
    });

    // Klick auf die Karte setzt nicht mehr direkt den Startpunkt, sondern zeigt eine
    // Mini-Auswahl (Startpunkt setzen / Abschnitt hier sperren / Punkt einschließen) - siehe
    // CONCEPT.md 6.18/6.19.
    map.on("click", (e) => {
      const { lat, lng } = e.lngLat;
      // "Für Verein vorschlagen" nur anbieten, wenn approved Mitglied - siehe
      // isApprovedClubMemberRef (CONCEPT.md Phase-4-Backlog "Mehrbenutzerfähigkeit/Auth/
      // Vereine", Stufe 2/3).
      const clubLockButton = isApprovedClubMemberRef.current
        ? '<button type="button" data-action="block-club" style="padding:6px 10px;cursor:pointer;color:' +
          CLUB_LOCK_COLOR +
          '">Für Verein vorschlagen</button>'
        : "";
      const popup = new Popup({ closeButton: true, closeOnClick: true, maxWidth: "none" })
        .setLngLat(e.lngLat)
        .setHTML(
          '<div style="display:flex;flex-direction:column;gap:4px;min-width:220px">' +
            '<button type="button" data-action="start" style="padding:6px 10px;cursor:pointer">Startpunkt setzen</button>' +
            '<button type="button" data-action="require" style="padding:6px 10px;cursor:pointer;color:#16a34a">Diesen Punkt in die Route einschließen</button>' +
            '<button type="button" data-action="block-temp" style="padding:6px 10px;cursor:pointer;color:#dc2626">Temporär sperren (nur diese Route)</button>' +
            '<button type="button" data-action="block-permanent" style="padding:6px 10px;cursor:pointer;color:' +
            PERSONAL_LOCK_COLOR +
            '">Dauerhaft für mich sperren</button>' +
            clubLockButton +
          "</div>",
        )
        .addTo(map);

      const el = popup.getElement();
      el.querySelector('[data-action="start"]')?.addEventListener("click", () => {
        onStartPointChangeRef.current({ lat, lon: lng });
        popup.remove();
      });
      el.querySelector('[data-action="require"]')?.addEventListener("click", () => {
        onAddRequiredPointRef.current({ lat, lon: lng });
        popup.remove();
      });
      el.querySelector('[data-action="block-temp"]')?.addEventListener("click", () => {
        onAddBlockedAreaRef.current({ lat, lon: lng, radiusMeters: DEFAULT_BLOCK_RADIUS_METERS });
        popup.remove();
      });
      el.querySelector('[data-action="block-permanent"]')?.addEventListener("click", () => {
        onCreatePersonalLockRef.current({ lat, lon: lng }).catch(() => {});
        popup.remove();
      });
      el.querySelector('[data-action="block-club"]')?.addEventListener("click", () => {
        onProposeClubLockRef.current({ lat, lon: lng }).catch(() => {});
        popup.remove();
      });
    });

    // MapLibre sizes its canvas from the container at construction time; in a flex
    // layout the container may not have its final size yet at that point, so the
    // canvas can get stuck at a small default. Watch for size changes explicitly.
    const resizeObserver = new ResizeObserver(() => map.resize());
    resizeObserver.observe(containerRef.current);

    return () => {
      resizeObserver.disconnect();
      map.remove();
      mapRef.current = null;
      // Sonst zeigt die Marker-Referenz nach einem Map-Neuaufbau (z.B. React StrictMode's
      // doppeltes Effekt-Mounting in Dev) noch auf das entfernte Kartenobjekt und es wird nie
      // ein neuer Marker angelegt.
      startMarkerRef.current = null;
    };
  }, []);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !startPoint) return;

    if (!startMarkerRef.current) {
      startMarkerRef.current = new Marker({ color: "#2563eb" })
        .setLngLat([startPoint.lon, startPoint.lat])
        .addTo(map);
    } else {
      startMarkerRef.current.setLngLat([startPoint.lon, startPoint.lat]);
    }
  }, [startPoint]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;

    const applyBlockedAreas = () => {
      const geojson: FeatureCollection<Point> = {
        type: "FeatureCollection",
        features: blockedAreas.map((b) => ({
          type: "Feature",
          properties: {},
          geometry: { type: "Point", coordinates: [b.lon, b.lat] },
        })),
      };
      const existing = map.getSource("blocked-areas") as GeoJSONSource | undefined;
      if (existing) {
        existing.setData(geojson);
      } else {
        map.addSource("blocked-areas", { type: "geojson", data: geojson });
        map.addLayer({
          id: "blocked-areas-circle",
          type: "circle",
          source: "blocked-areas",
          paint: {
            "circle-radius": 14,
            "circle-color": "#dc2626",
            "circle-opacity": 0.35,
            "circle-stroke-color": "#dc2626",
            "circle-stroke-width": 2,
          },
        });
      }
    };

    if (styleReadyRef.current) {
      applyBlockedAreas();
    } else {
      map.once("load", applyBlockedAreas);
    }
  }, [blockedAreas]);

  // Persistierte Sperr-Bereiche (persoenlich + Verein, siehe SegmentLock/CONCEPT.md
  // Phase-4-Backlog "Mehrbenutzerfähigkeit/Auth/Vereine", Stufe 3) - analog zum bestehenden
  // blockedAreas-Kreis-Layer oben, aber mit einer nach "scope" unterscheidenden Farbe statt
  // einer festen.
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;

    const applySegmentLocks = () => {
      const geojson: FeatureCollection<Point> = {
        type: "FeatureCollection",
        features: segmentLocks.map((s) => ({
          type: "Feature",
          properties: { scope: s.scope },
          geometry: { type: "Point", coordinates: [s.lon, s.lat] },
        })),
      };
      const existing = map.getSource("segment-locks") as GeoJSONSource | undefined;
      if (existing) {
        existing.setData(geojson);
      } else {
        map.addSource("segment-locks", { type: "geojson", data: geojson });
        map.addLayer({
          id: "segment-locks-circle",
          type: "circle",
          source: "segment-locks",
          paint: {
            "circle-radius": 14,
            "circle-color": ["match", ["get", "scope"], "club", CLUB_LOCK_COLOR, PERSONAL_LOCK_COLOR],
            "circle-opacity": 0.35,
            "circle-stroke-color": ["match", ["get", "scope"], "club", CLUB_LOCK_COLOR, PERSONAL_LOCK_COLOR],
            "circle-stroke-width": 2,
          },
        });
      }
    };

    if (styleReadyRef.current) {
      applySegmentLocks();
    } else {
      map.once("load", applySegmentLocks);
    }
  }, [segmentLocks]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;

    const applyRequiredPoints = () => {
      const geojson: FeatureCollection<Point> = {
        type: "FeatureCollection",
        features: requiredPoints.map((p) => ({
          type: "Feature",
          properties: {},
          geometry: { type: "Point", coordinates: [p.lon, p.lat] },
        })),
      };
      const existing = map.getSource("required-points") as GeoJSONSource | undefined;
      if (existing) {
        existing.setData(geojson);
      } else {
        map.addSource("required-points", { type: "geojson", data: geojson });
        map.addLayer({
          id: "required-points-circle",
          type: "circle",
          source: "required-points",
          paint: {
            "circle-radius": 8,
            "circle-color": "#16a34a",
            "circle-stroke-color": "#ffffff",
            "circle-stroke-width": 2,
          },
        });
      }
    };

    if (styleReadyRef.current) {
      applyRequiredPoints();
    } else {
      map.once("load", applyRequiredPoints);
    }
  }, [requiredPoints]);

  // Baustellen-Sperrungen-Layer (VIZ Berlin, CONCEPT.md 6.27): orange Linie entlang der Straße
  // fuer Baustellen mit LineString-Geometrie, orange Kreis fuer die selteneren punktförmigen
  // Fälle - analog zum roten BlockedArea-Kreis, aber automatisch befüllt statt vom Nutzer
  // gesetzt. Vom Nutzer ignorierte Einträge (siehe App.tsx toggleIgnoredClosure) werden stark
  // abgeblendet dargestellt statt entfernt, damit sichtbar bleibt, DASS dort eine erkannte
  // Baustelle liegt, die der Nutzer bewusst übersteuert hat. Ein-/ausblendbar wie der
  // Ampeln/Stoppschilder-Layer (initiale Sichtbarkeit hier gesetzt, weiteres Umschalten ueber
  // den separaten Effekt unten, der nur die "visibility"-Layout-Property umschaltet).
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;

    const applyConstructionClosures = () => {
      const lineFeatures: FeatureCollection<LineString>["features"] = [];
      const pointFeatures: FeatureCollection<Point>["features"] = [];
      for (const closure of constructionClosures) {
        const ignored = ignoredClosureIds.has(closure.id);
        if (closure.geometry.length > 1) {
          lineFeatures.push({
            type: "Feature",
            properties: { ignored },
            geometry: { type: "LineString", coordinates: closure.geometry.map((p) => [p.lon, p.lat]) },
          });
        } else if (closure.geometry.length === 1) {
          pointFeatures.push({
            type: "Feature",
            properties: { ignored },
            geometry: { type: "Point", coordinates: [closure.geometry[0].lon, closure.geometry[0].lat] },
          });
        }
      }

      const lineGeojson: FeatureCollection<LineString> = { type: "FeatureCollection", features: lineFeatures };
      const existingLineSource = map.getSource("construction-closures-lines") as GeoJSONSource | undefined;
      if (existingLineSource) {
        existingLineSource.setData(lineGeojson);
      } else {
        map.addSource("construction-closures-lines", { type: "geojson", data: lineGeojson });
        map.addLayer({
          id: "construction-closures-line",
          type: "line",
          source: "construction-closures-lines",
          layout: { visibility: showConstructionClosuresRef.current ? "visible" : "none" },
          paint: {
            "line-color": CONSTRUCTION_CLOSURE_COLOR,
            "line-width": 5,
            "line-opacity": ["case", ["get", "ignored"], 0.25, 0.85],
          },
        });
      }

      const pointGeojson: FeatureCollection<Point> = { type: "FeatureCollection", features: pointFeatures };
      const existingPointSource = map.getSource("construction-closures-points") as GeoJSONSource | undefined;
      if (existingPointSource) {
        existingPointSource.setData(pointGeojson);
      } else {
        map.addSource("construction-closures-points", { type: "geojson", data: pointGeojson });
        map.addLayer({
          id: "construction-closures-circle",
          type: "circle",
          source: "construction-closures-points",
          layout: { visibility: showConstructionClosuresRef.current ? "visible" : "none" },
          paint: {
            "circle-radius": 10,
            "circle-color": CONSTRUCTION_CLOSURE_COLOR,
            "circle-opacity": ["case", ["get", "ignored"], 0.15, 0.45],
            "circle-stroke-color": CONSTRUCTION_CLOSURE_COLOR,
            "circle-stroke-width": 2,
          },
        });
      }
    };

    if (styleReadyRef.current) {
      applyConstructionClosures();
    } else {
      map.once("load", applyConstructionClosures);
    }
  }, [constructionClosures, ignoredClosureIds]);

  // Ampeln/Stoppschilder-Layer (CONCEPT.md 6.21): einmaliger Abruf pro Kartensitzung (nicht bei
  // jedem Ein-/Ausblenden neu geladen), Sichtbarkeit danach nur ueber die MapLibre-
  // "visibility"-Layout-Property umgeschaltet.
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    let cancelled = false;

    const addJunctionsLayer = async () => {
      let junctions: Junction[];
      try {
        junctions = await requestJunctions(authToken);
      } catch {
        return; // Kein hartes UI-Fehlerfeedback fuer diesen rein informativen Layer.
      }
      if (cancelled || map.getSource("junctions")) return;

      const geojson: FeatureCollection<Point> = {
        type: "FeatureCollection",
        features: junctions.map((j) => ({
          type: "Feature",
          properties: { type: j.type },
          geometry: { type: "Point", coordinates: [j.point.lon, j.point.lat] },
        })),
      };
      map.addSource("junctions", { type: "geojson", data: geojson });
      map.addLayer({
        id: "junctions-circle",
        type: "circle",
        source: "junctions",
        layout: { visibility: showJunctionsRef.current ? "visible" : "none" },
        paint: {
          "circle-radius": 4,
          "circle-color": ["match", ["get", "type"], "TrafficSignal", "#dc2626", "Stop", "#ea580c", "#64748b"],
          "circle-stroke-color": "#ffffff",
          "circle-stroke-width": 1,
        },
      });
    };

    if (styleReadyRef.current) {
      addJunctionsLayer();
    } else {
      map.once("load", addJunctionsLayer);
    }

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !map.getLayer("junctions-circle")) return;
    map.setLayoutProperty("junctions-circle", "visibility", showJunctions ? "visible" : "none");
  }, [showJunctions]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    const visibility = showConstructionClosures ? "visible" : "none";
    if (map.getLayer("construction-closures-line")) {
      map.setLayoutProperty("construction-closures-line", "visibility", visibility);
    }
    if (map.getLayer("construction-closures-circle")) {
      map.setLayoutProperty("construction-closures-circle", "visibility", visibility);
    }
  }, [showConstructionClosures]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;

    const applyRoute = () => {
      // Untergrund-Warnung (breiter, halbtransparenter roter "Halo") - wird VOR der Basis-Route
      // angelegt, damit sie in der Layer-Reihenfolge darunter liegt (dünne blaue Linie und die
      // Intervall-Einfärbung bleiben obenauf sichtbar).
      const unpavedSegments = (surfaceSegments ?? []).filter((s) => UNPAVED_SURFACES.has(s.surface));
      const surfaceGeojson: FeatureCollection<LineString> = {
        type: "FeatureCollection",
        features: unpavedSegments.map((s) => ({
          type: "Feature",
          properties: { surface: s.surface },
          geometry: { type: "LineString", coordinates: s.geometry.map((p) => [p.lon, p.lat]) },
        })),
      };
      const existingSurfaceSource = map.getSource("surface-warning") as GeoJSONSource | undefined;
      if (existingSurfaceSource) {
        existingSurfaceSource.setData(surfaceGeojson);
      } else {
        map.addSource("surface-warning", { type: "geojson", data: surfaceGeojson });
        map.addLayer({
          id: "surface-warning-line",
          type: "line",
          source: "surface-warning",
          paint: { "line-color": SURFACE_WARNING_COLOR, "line-width": 9, "line-opacity": 0.55 },
        });
      }

      // Rauer-Belag-Warnung (Analog zur Untergrund-Warnung oben, eigene Farbe/Layer, siehe
      // ROUGH_SURFACE_WARNING_COLOR) - ebenfalls VOR der Basis-Route angelegt.
      const roughSegments = (smoothnessSegments ?? []).filter((s) => BAD_SMOOTHNESS_VALUES.has(s.surface));
      const roughGeojson: FeatureCollection<LineString> = {
        type: "FeatureCollection",
        features: roughSegments.map((s) => ({
          type: "Feature",
          properties: { smoothness: s.surface },
          geometry: { type: "LineString", coordinates: s.geometry.map((p) => [p.lon, p.lat]) },
        })),
      };
      const existingRoughSource = map.getSource("rough-warning") as GeoJSONSource | undefined;
      if (existingRoughSource) {
        existingRoughSource.setData(roughGeojson);
      } else {
        map.addSource("rough-warning", { type: "geojson", data: roughGeojson });
        map.addLayer({
          id: "rough-warning-line",
          type: "line",
          source: "rough-warning",
          paint: { "line-color": ROUGH_SURFACE_WARNING_COLOR, "line-width": 9, "line-opacity": 0.55 },
        });
      }

      // Basis-Route (blau, duenn) - immer die volle Strecke, darauf liegen die
      // hervorgehobenen Intervall-Segmente aus dem Trainingsplan.
      const existingRouteSource = map.getSource("route") as GeoJSONSource | undefined;
      const routeGeojson: FeatureCollection<LineString> = {
        type: "FeatureCollection",
        features: routeGeometry && routeGeometry.length > 0
          ? [{
              type: "Feature",
              properties: {},
              geometry: { type: "LineString", coordinates: routeGeometry.map((p) => [p.lon, p.lat]) },
            }]
          : [],
      };

      if (existingRouteSource) {
        existingRouteSource.setData(routeGeojson);
      } else {
        map.addSource("route", { type: "geojson", data: routeGeojson });
        map.addLayer({
          id: "route-line",
          type: "line",
          source: "route",
          paint: { "line-color": "#2563eb", "line-width": 3 },
        });
      }

      const segments = routeSegments ?? [];
      const segmentsGeojson: FeatureCollection<LineString> = {
        type: "FeatureCollection",
        features: segments.map((s) => ({
          type: "Feature",
          properties: { label: s.label },
          geometry: { type: "LineString", coordinates: s.geometry.map((p) => [p.lon, p.lat]) },
        })),
      };
      const existingSegmentsSource = map.getSource("route-segments") as GeoJSONSource | undefined;
      if (existingSegmentsSource) {
        existingSegmentsSource.setData(segmentsGeojson);
      } else {
        map.addSource("route-segments", { type: "geojson", data: segmentsGeojson });
        map.addLayer({
          id: "route-segments-line",
          type: "line",
          source: "route-segments",
          paint: { "line-color": SEGMENT_FALLBACK_COLOR, "line-width": 6 },
        });
      }
      if (map.getLayer("route-segments-line")) {
        map.setPaintProperty(
          "route-segments-line",
          "line-color",
          segmentColorExpression(segments.map((s) => s.label)),
        );
      }

      if (!routeGeometry || routeGeometry.length === 0) return;
      const bounds = routeGeometry.reduce(
        (b, p) => b.extend([p.lon, p.lat]),
        new LngLatBounds([routeGeometry[0].lon, routeGeometry[0].lat], [routeGeometry[0].lon, routeGeometry[0].lat]),
      );
      map.fitBounds(bounds, { padding: 40 });
    };

    if (styleReadyRef.current) {
      applyRoute();
    } else {
      map.once("load", applyRoute);
    }
  }, [routeGeometry, routeSegments, surfaceSegments, smoothnessSegments]);

  return <div ref={containerRef} style={{ width: "100%", height: "100%" }} />;
}
