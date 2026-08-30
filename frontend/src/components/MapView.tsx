import { useEffect, useRef } from "react";
import { Map as MapLibreMap, Marker, LngLatBounds } from "maplibre-gl";
import type { StyleSpecification, GeoJSONSource, ExpressionSpecification } from "maplibre-gl";
import "maplibre-gl/dist/maplibre-gl.css";
import type { FeatureCollection, LineString } from "geojson";
import type { GeoPoint, RouteSegment } from "../types";

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
}

export function MapView({ startPoint, onStartPointChange, routeGeometry, routeSegments }: MapViewProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<MapLibreMap | null>(null);
  const startMarkerRef = useRef<Marker | null>(null);
  const onStartPointChangeRef = useRef(onStartPointChange);
  onStartPointChangeRef.current = onStartPointChange;
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

    map.on("click", (e) => {
      onStartPointChangeRef.current({ lat: e.lngLat.lat, lon: e.lngLat.lng });
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

    const applyRoute = () => {
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
  }, [routeGeometry, routeSegments]);

  return <div ref={containerRef} style={{ width: "100%", height: "100%" }} />;
}
