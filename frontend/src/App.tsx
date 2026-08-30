import { useEffect, useState } from "react";
import { MapView, colorForSegmentLabel } from "./components/MapView";
import { WorkoutEditor } from "./components/WorkoutEditor";
import { AuthPanel } from "./components/AuthPanel";
import {
  requestRoute,
  requestRouteGpx,
  buildWorkoutFitFile,
  registerUser,
  loginUser,
  fetchCurrentUser,
  fetchProfile,
  saveProfile,
} from "./api";
import type { BlockedArea, GeoPoint, RouteResult, SegmentReusePreference, WorkoutBlockSpec } from "./types";
import "./App.css";

const AUTH_TOKEN_STORAGE_KEY = "wattloop_auth_token";

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

function parseOptionalMeters(value: string): number | null {
  return value.trim() === "" ? null : Number(value);
}

const COMPASS_DIRECTIONS = ["Nord", "Nordost", "Ost", "Südost", "Süd", "Südwest", "West", "Nordwest"];

// windFromDirectionDegrees folgt meteorologischer Konvention (Richtung, AUS der der Wind
// weht) - siehe RouteResult.Wind (Backend).
function formatWind(windSpeedMps: number, windFromDirectionDegrees: number): string {
  const kmh = Math.round(windSpeedMps * 3.6);
  const index = Math.round(windFromDirectionDegrees / 45) % 8;
  return `${kmh} km/h aus ${COMPASS_DIRECTIONS[index]}`;
}

function downloadBlob(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(url);
}

type InputMode = "file" | "editor";

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
  // Leerer String = kein Limit (siehe parseOptionalMeters) - kein sinnvoller Zahlen-Default,
  // da 0 etwas anderes bedeuten wuerde (gar kein unbefestigter Untergrund erlaubt).
  const [maxUnpavedSegmentMeters, setMaxUnpavedSegmentMeters] = useState("");
  const [maxTotalUnpavedMeters, setMaxTotalUnpavedMeters] = useState("");
  const [maxDisruptiveJunctions, setMaxDisruptiveJunctions] = useState("");
  const [maxRouteVariantAttempts, setMaxRouteVariantAttempts] = useState("");
  // Leer = keine Windvorhersage (siehe RouteFormInput.plannedStartTime) - optional, nicht
  // erzwungen, da Wind ein rein additives Zeitschaetzungs-Feature ist.
  const [plannedStartTime, setPlannedStartTime] = useState("");
  const [blockedAreas, setBlockedAreas] = useState<BlockedArea[]>([]);
  const [requiredPoints, setRequiredPoints] = useState<GeoPoint[]>([]);

  function addBlockedArea(area: BlockedArea) {
    setBlockedAreas((prev) => [...prev, area]);
  }

  function removeBlockedArea(index: number) {
    setBlockedAreas((prev) => prev.filter((_, i) => i !== index));
  }

  function addRequiredPoint(point: GeoPoint) {
    setRequiredPoints((prev) => [...prev, point]);
  }

  function removeRequiredPoint(index: number) {
    setRequiredPoints((prev) => prev.filter((_, i) => i !== index));
  }

  const [authEmail, setAuthEmail] = useState<string | null>(null);
  const [authToken, setAuthToken] = useState<string | null>(null);
  const [authLoading, setAuthLoading] = useState(false);
  const [authError, setAuthError] = useState<string | null>(null);
  // Verhindert, dass beim Laden kurz das Login-Formular aufblitzt, waehrend ein gespeichertes
  // Token noch gegen /auth/me geprueft wird - true bis diese Pruefung (oder deren Fehlen)
  // abgeschlossen ist.
  const [authInitializing, setAuthInitializing] = useState(true);

  // Login ist Pflicht (WattLoop ohne Konto nicht nutzbar) - das gespeicherte Fahrerprofil ist
  // der erste konkrete Nutzen davon (siehe CONCEPT.md 6.25).
  async function loadProfile(token: string) {
    const profile = await fetchProfile(token).catch(() => null);
    if (profile) {
      setFtpWatts(profile.ftpWatts);
      setWeightKg(profile.weightKg);
      setSprintAvgWatts(profile.sprintAvgWatts);
    }
  }

  // Ein gespeichertes Token ueberlebt einen Seiten-Reload nur, wenn es noch gueltig ist - beim
  // Laden einmal gegen /auth/me pruefen statt blind zu vertrauen (kann z.B. abgelaufen sein).
  useEffect(() => {
    const storedToken = localStorage.getItem(AUTH_TOKEN_STORAGE_KEY);
    if (!storedToken) {
      setAuthInitializing(false);
      return;
    }
    fetchCurrentUser(storedToken).then(async (email) => {
      if (email) {
        setAuthEmail(email);
        setAuthToken(storedToken);
        await loadProfile(storedToken);
      } else {
        localStorage.removeItem(AUTH_TOKEN_STORAGE_KEY);
      }
      setAuthInitializing(false);
    });
  }, []);

  async function handleLogin(email: string, password: string) {
    setAuthLoading(true);
    setAuthError(null);
    try {
      const result = await loginUser(email, password);
      localStorage.setItem(AUTH_TOKEN_STORAGE_KEY, result.token);
      setAuthToken(result.token);
      setAuthEmail(result.email);
      await loadProfile(result.token);
    } catch (err) {
      setAuthError(err instanceof Error ? err.message : String(err));
    } finally {
      setAuthLoading(false);
    }
  }

  async function handleRegister(email: string, password: string) {
    setAuthLoading(true);
    setAuthError(null);
    try {
      await registerUser(email, password);
      await handleLogin(email, password);
    } catch (err) {
      setAuthError(err instanceof Error ? err.message : String(err));
      setAuthLoading(false);
    }
  }

  function handleLogout() {
    localStorage.removeItem(AUTH_TOKEN_STORAGE_KEY);
    setAuthToken(null);
    setAuthEmail(null);
  }

  const [showJunctions, setShowJunctions] = useState(false);

  const [inputMode, setInputMode] = useState<InputMode>("file");
  const [fitFile, setFitFile] = useState<File | null>(null);
  const [editorBlocks, setEditorBlocks] = useState<WorkoutBlockSpec[]>([]);

  const [routeResult, setRouteResult] = useState<RouteResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const hasWorkoutInput = inputMode === "file" ? !!fitFile : editorBlocks.length > 0;

  async function resolveFitFile(token: string): Promise<File> {
    if (inputMode === "file") {
      if (!fitFile) throw new Error("Keine FIT-Datei ausgewählt.");
      return fitFile;
    }
    return buildWorkoutFitFile(editorBlocks, token);
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!startPoint || !hasWorkoutInput || !authToken) return;

    setLoading(true);
    setError(null);
    setRouteResult(null);
    try {
      const file = await resolveFitFile(authToken);
      const result = await requestRoute(
        {
          rider: { ftpWatts, weightKg, sprintAvgWatts },
          startLat: startPoint.lat,
          startLon: startPoint.lon,
          maxApproachMinutes,
          segmentReuse,
          allowUTurns,
          maxUnpavedSegmentMeters: parseOptionalMeters(maxUnpavedSegmentMeters),
          maxTotalUnpavedMeters: parseOptionalMeters(maxTotalUnpavedMeters),
          maxDisruptiveJunctions: parseOptionalMeters(maxDisruptiveJunctions),
          maxRouteVariantAttempts: parseOptionalMeters(maxRouteVariantAttempts),
          blockedAreas,
          requiredPoints,
          plannedStartTime: plannedStartTime || null,
          fitFile: file,
        },
        authToken,
      );
      setRouteResult(result);
      // Nicht blockierend/nicht kritisch - ein fehlgeschlagenes Speichern soll die gerade
      // erfolgreich berechnete Route nicht verwerfen (siehe CONCEPT.md 6.25).
      saveProfile(authToken, { ftpWatts, weightKg, sprintAvgWatts }).catch(() => {});
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  async function handleGpxDownload() {
    if (!startPoint || !hasWorkoutInput || !authToken) return;
    setError(null);
    try {
      const file = await resolveFitFile(authToken);
      const blob = await requestRouteGpx(
        {
          rider: { ftpWatts, weightKg, sprintAvgWatts },
          startLat: startPoint.lat,
          startLon: startPoint.lon,
          maxApproachMinutes,
          segmentReuse,
          allowUTurns,
          maxUnpavedSegmentMeters: parseOptionalMeters(maxUnpavedSegmentMeters),
          maxTotalUnpavedMeters: parseOptionalMeters(maxTotalUnpavedMeters),
          maxDisruptiveJunctions: parseOptionalMeters(maxDisruptiveJunctions),
          maxRouteVariantAttempts: parseOptionalMeters(maxRouteVariantAttempts),
          blockedAreas,
          requiredPoints,
          plannedStartTime: plannedStartTime || null,
          fitFile: file,
        },
        authToken,
      );
      downloadBlob(blob, "trainingsroute.gpx");
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  // Login ist Pflicht (siehe CONCEPT.md 6.25) - vor Abschluss der Token-Pruefung (kurzer
  // Moment beim Laden) noch nichts anzeigen, um ein kurzes Aufblitzen des Login-Formulars bei
  // eigentlich gueltigem gespeichertem Token zu vermeiden.
  if (authInitializing) {
    return <div className="app-layout" />;
  }

  if (!authEmail || !authToken) {
    return (
      <div className="auth-gate">
        <h1>WattLoop</h1>
        <AuthPanel
          email={null}
          loading={authLoading}
          error={authError}
          onLogin={handleLogin}
          onRegister={handleRegister}
          onLogout={handleLogout}
        />
      </div>
    );
  }

  return (
    <div className="app-layout">
      <aside className="sidebar">
        <h1>WattLoop</h1>

        <AuthPanel
          email={authEmail}
          loading={authLoading}
          error={authError}
          onLogin={handleLogin}
          onRegister={handleRegister}
          onLogout={handleLogout}
        />

        <form onSubmit={handleSubmit}>
          <label>
            Startpunkt
            <input
              type="text"
              readOnly
              value={startPoint ? `${startPoint.lat.toFixed(5)}, ${startPoint.lon.toFixed(5)}` : "auf Karte klicken"}
            />
          </label>
          <p className="hint">
            Auf die Karte klicken, um den Startpunkt zu setzen, einen Punkt in die Route
            einzuschließen oder einen Abschnitt zu sperren.
          </p>

          <div className="mode-tabs">
            <button
              type="button"
              className={inputMode === "file" ? "active" : ""}
              onClick={() => setInputMode("file")}
            >
              FIT-Datei hochladen
            </button>
            <button
              type="button"
              className={inputMode === "editor" ? "active" : ""}
              onClick={() => setInputMode("editor")}
            >
              Workout zusammenstellen
            </button>
          </div>

          {inputMode === "file" ? (
            <label>
              FIT-Workout-Datei
              <input
                type="file"
                accept=".fit"
                onChange={(e) => setFitFile(e.target.files?.[0] ?? null)}
              />
            </label>
          ) : (
            <WorkoutEditor onChange={setEditorBlocks} />
          )}

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
            <label className="checkbox-label">
              <input
                type="checkbox"
                checked={showJunctions}
                onChange={(e) => setShowJunctions(e.target.checked)}
              />
              Ampeln/Stoppschilder auf der Karte anzeigen
            </label>
            <label>
              Max. Länge je unbefestigtem Abschnitt (m)
              <input
                type="number"
                value={maxUnpavedSegmentMeters}
                onChange={(e) => setMaxUnpavedSegmentMeters(e.target.value)}
                min={0}
                placeholder="kein Limit"
              />
            </label>
            <label>
              Max. unbefestigte Strecke insgesamt (m)
              <input
                type="number"
                value={maxTotalUnpavedMeters}
                onChange={(e) => setMaxTotalUnpavedMeters(e.target.value)}
                min={0}
                placeholder="kein Limit"
              />
            </label>
            <label>
              Max. Anzahl Ampeln/Kreuzungen
              <input
                type="number"
                value={maxDisruptiveJunctions}
                onChange={(e) => setMaxDisruptiveJunctions(e.target.value)}
                min={0}
                placeholder="kein Limit"
              />
            </label>
            <label>
              Anzahl Streckenvarianten (nur mit Limit oben relevant)
              <input
                type="number"
                value={maxRouteVariantAttempts}
                onChange={(e) => setMaxRouteVariantAttempts(e.target.value)}
                min={1}
                placeholder="Standard: 10"
              />
            </label>
            <label>
              Geplanter Fahrzeitpunkt (für Windschätzung)
              <input
                type="datetime-local"
                value={plannedStartTime}
                onChange={(e) => setPlannedStartTime(e.target.value)}
              />
            </label>
          </fieldset>

          {requiredPoints.length > 0 && (
            <fieldset>
              <legend>Pflicht-Wegpunkte</legend>
              <ul className="point-list">
                {requiredPoints.map((point, i) => (
                  <li key={i}>
                    {point.lat.toFixed(5)}, {point.lon.toFixed(5)}
                    <button type="button" onClick={() => removeRequiredPoint(i)}>
                      Entfernen
                    </button>
                  </li>
                ))}
              </ul>
            </fieldset>
          )}

          {blockedAreas.length > 0 && (
            <fieldset>
              <legend>Gesperrte Abschnitte</legend>
              <ul className="point-list">
                {blockedAreas.map((area, i) => (
                  <li key={i}>
                    {area.lat.toFixed(5)}, {area.lon.toFixed(5)} ({area.radiusMeters} m)
                    <button type="button" onClick={() => removeBlockedArea(i)}>
                      Entfernen
                    </button>
                  </li>
                ))}
              </ul>
            </fieldset>
          )}

          <button type="submit" disabled={loading || !startPoint || !hasWorkoutInput}>
            {loading ? "Route wird berechnet…" : "Route berechnen"}
          </button>
        </form>

        {error && <p className="error">{error}</p>}

        {routeResult && (
          <div className="result">
            <h2>Ergebnis</h2>
            <p>Distanz: {(routeResult.totalDistanceMeters / 1000).toFixed(1)} km</p>
            <p>Geschätzte Zeit: {formatDotNetTimeSpan(routeResult.estimatedTotalTime)}</p>
            {routeResult.wind && (
              <p>Wind: {formatWind(routeResult.wind.windSpeedMps, routeResult.wind.windFromDirectionDegrees)}</p>
            )}

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
          surfaceSegments={routeResult?.surfaceSegments ?? null}
          blockedAreas={blockedAreas}
          onAddBlockedArea={addBlockedArea}
          requiredPoints={requiredPoints}
          onAddRequiredPoint={addRequiredPoint}
          showJunctions={showJunctions}
          authToken={authToken}
        />
      </main>
    </div>
  );
}
