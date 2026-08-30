import { useEffect, useRef } from "react";
import { Map as MapLibreMap, Marker, LngLatBounds } from "maplibre-gl";
import type { StyleSpecification, GeoJSONSource } from "maplibre-gl";
import "maplibre-gl/dist/maplibre-gl.css";
import type { GeoPoint } from "../types";

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

interface MapViewProps {
  startPoint: GeoPoint | null;
  onStartPointChange: (point: GeoPoint) => void;
  routeGeometry: GeoPoint[] | null;
}

export function MapView({ startPoint, onStartPointChange, routeGeometry }: MapViewProps) {
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
      const existingSource = map.getSource("route") as GeoJSONSource | undefined;
      if (!routeGeometry || routeGeometry.length === 0) {
        if (existingSource) {
          existingSource.setData({ type: "FeatureCollection", features: [] });
        }
        return;
      }

      const geojson: GeoJSON.Feature<GeoJSON.LineString> = {
        type: "Feature",
        properties: {},
        geometry: {
          type: "LineString",
          coordinates: routeGeometry.map((p) => [p.lon, p.lat]),
        },
      };

      if (existingSource) {
        existingSource.setData(geojson);
      } else {
        map.addSource("route", { type: "geojson", data: geojson });
        map.addLayer({
          id: "route-line",
          type: "line",
          source: "route",
          paint: { "line-color": "#dc2626", "line-width": 4 },
        });
      }

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
  }, [routeGeometry]);

  return <div ref={containerRef} style={{ width: "100%", height: "100%" }} />;
}
