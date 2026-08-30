import { useState } from "react";
import { MapView, colorForSegmentLabel } from "./components/MapView";
import { requestRoute, requestRouteGpx } from "./api";
import type { GeoPoint, RouteResult, SegmentReusePreference } from "./types";
import "./App.css";

function formatDotNetTimeSpan(value: string): string {
  const match = /^(\d+)\.(\d{2}):(\d{2}):(\d{2})|^(\d{2}):(\d{2}):(\d{2})/.exec(value);
  if (!match) return value;
  const days = match[1] ? Number(match[1]) : 0;
  const hours = Number(match[2] ?? match[5] ?? 0);
  const minutes = Number(match[3] ?? match[6] ?? 0);
  const totalMinutes = days * 24 * 60 + hours * 60 + minutes;
  const h = Math.floor(totalMinutes / 60);
  const m = totalMinutes % 60;
  return h > 0 ? `${h} h ${m} min` : `${m} min`;
}

function downloadBlob(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(url);
}

export default function App() {
  const [startPoint, setStartPoint] = useState<GeoPoint | null>({
    lat: 52.5426187,
    lon: 13.4763778,
  });
  const [ftpWatts, setFtpWatts] = useState(250);
  const [weightKg, setWeightKg] = useState(75);
  const [sprintAvgWatts, setSprintAvgWatts] = useState(800);
  const [maxApproachMinutes, setMaxApproachMinutes] = useState(30);
  const [segmentReuse, setSegmentReuse] = useState<SegmentReusePreference>("PreferReuse");
  const [allowUTurns, setAllowUTurns] = useState(true);
  const [fitFile, setFitFile] = useState<File | null>(null);

  const [routeResult, setRouteResult] = useState<RouteResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!startPoint || !fitFile) return;

    setLoading(true);
    setError(null);
    setRouteResult(null);
    try {
      const result = await requestRoute({
        rider: { ftpWatts, weightKg, sprintAvgWatts },
        startLat: startPoint.lat,
        startLon: startPoint.lon,
        maxApproachMinutes,
        segmentReuse,
        allowUTurns,
        fitFile,
      });
      setRouteResult(result);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  async function handleGpxDownload() {
    if (!startPoint || !fitFile) return;
    setError(null);
    try {
      const blob = await requestRouteGpx({
        rider: { ftpWatts, weightKg, sprintAvgWatts },
        startLat: startPoint.lat,
        startLon: startPoint.lon,
        maxApproachMinutes,
        segmentReuse,
        allowUTurns,
        fitFile,
      });
      downloadBlob(blob, "trainingsroute.gpx");
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  return (
    <div className="app-layout">
      <aside className="sidebar">
        <h1>Trainingsrouten-Planer</h1>

        <form onSubmit={handleSubmit}>
          <label>
            Startpunkt
            <input
              type="text"
              readOnly
              value={startPoint ? `${startPoint.lat.toFixed(5)}, ${startPoint.lon.toFixed(5)}` : "auf Karte klicken"}
            />
          </label>

          <label>
            FIT-Workout-Datei
            <input
              type="file"
              accept=".fit"
              onChange={(e) => setFitFile(e.target.files?.[0] ?? null)}
              required
            />
          </label>

          <fieldset>
            <legend>Nutzerprofil</legend>
            <label>
              FTP (Watt)
              <input type="number" value={ftpWatts} onChange={(e) => setFtpWatts(Number(e.target.value))} min={1} required />
            </label>
            <label>
              Gewicht (kg)
              <input type="number" value={weightKg} onChange={(e) => setWeightKg(Number(e.target.value))} min={1} required />
            </label>
            <label>
              Sprint Ø-Watt
              <input type="number" value={sprintAvgWatts} onChange={(e) => setSprintAvgWatts(Number(e.target.value))} min={1} required />
            </label>
          </fieldset>

          <fieldset>
            <legend>Einstellungen</legend>
            <label>
              Max. Anfahrtszeit (min)
              <input
                type="number"
                value={maxApproachMinutes}
                onChange={(e) => setMaxApproachMinutes(Number(e.target.value))}
                min={0}
              />
            </label>
            <label>
              Intervall-Wiederholung
              <select value={segmentReuse} onChange={(e) => setSegmentReuse(e.target.value as SegmentReusePreference)}>
                <option value="PreferReuse">Gleicher Ort</option>
                <option value="PreferVariety">Streckenvielfalt</option>
              </select>
            </label>
            <label className="checkbox-label">
              <input
                type="checkbox"
                checked={allowUTurns}
                onChange={(e) => setAllowUTurns(e.target.checked)}
              />
              Kehrtwenden erlauben
            </label>
          </fieldset>

          <button type="submit" disabled={loading || !startPoint || !fitFile}>
            {loading ? "Route wird berechnet…" : "Route berechnen"}
          </button>
        </form>

        {error && <p className="error">{error}</p>}

        {routeResult && (
          <div className="result">
            <h2>Ergebnis</h2>
            <p>Distanz: {(routeResult.totalDistanceMeters / 1000).toFixed(1)} km</p>
            <p>Geschätzte Zeit: {formatDotNetTimeSpan(routeResult.estimatedTotalTime)}</p>

            {routeResult.segments.length > 0 && (
              <div className="legend">
                <strong>Intervalle:</strong>
                <ul>
                  {[...new Set(routeResult.segments.map((s) => s.label))].map((label) => (
                    <li key={label}>
                      <span
                        className="legend-swatch"
                        style={{
                          background: colorForSegmentLabel(
                            label,
                            routeResult.segments.map((s) => s.label),
                          ),
                        }}
                      />
                      {label}
                    </li>
                  ))}
                </ul>
              </div>
            )}

            {routeResult.warnings.length > 0 && (
              <div className="warnings">
                <strong>Hinweise:</strong>
                <ul>
                  {routeResult.warnings.map((w, i) => (
                    <li key={i}>{w.message}</li>
                  ))}
                </ul>
              </div>
            )}

            <button type="button" onClick={handleGpxDownload}>
              Als GPX herunterladen
            </button>
          </div>
        )}
      </aside>

      <main className="map-container">
        <MapView
          startPoint={startPoint}
          onStartPointChange={setStartPoint}
          routeGeometry={routeResult?.geometry ?? null}
          routeSegments={routeResult?.segments ?? null}
        />
      </main>
    </div>
  );
}
