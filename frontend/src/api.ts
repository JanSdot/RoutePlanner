import type { AuthResponse, ConstructionClosure, Junction, RouteFormInput, RouteResult, WorkoutBlockSpec } from "./types";

// Zur Build-Zeit über Vite gesetzt (siehe .env.production / Render-Umgebungsvariable
// VITE_API_BASE_URL) - lokal ohne .env-Datei Fallback auf den lokalen API-Port.
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "http://localhost:5080";

function buildFormData(input: RouteFormInput, format: "json" | "gpx"): FormData {
  const data = new FormData();
  data.append("fitFile", input.fitFile);
  data.append("ftpWatts", String(input.rider.ftpWatts));
  data.append("weightKg", String(input.rider.weightKg));
  data.append("sprintAvgWatts", String(input.rider.sprintAvgWatts));
  data.append("startLat", String(input.startLat));
  data.append("startLon", String(input.startLon));
  data.append("maxApproachMinutes", String(input.maxApproachMinutes));
  data.append("segmentReuse", input.segmentReuse);
  data.append("allowUTurns", String(input.allowUTurns));
  if (input.maxUnpavedSegmentMeters != null) {
    data.append("maxUnpavedSegmentMeters", String(input.maxUnpavedSegmentMeters));
  }
  if (input.maxTotalUnpavedMeters != null) {
    data.append("maxTotalUnpavedMeters", String(input.maxTotalUnpavedMeters));
  }
  if (input.maxTotalRoughMeters != null) {
    data.append("maxTotalRoughMeters", String(input.maxTotalRoughMeters));
  }
  if (input.maxDisruptiveJunctions != null) {
    data.append("maxDisruptiveJunctions", String(input.maxDisruptiveJunctions));
  }
  if (input.maxRouteVariantAttempts != null) {
    data.append("maxRouteVariantAttempts", String(input.maxRouteVariantAttempts));
  }
  if (input.blockedAreas.length > 0) {
    data.append("blockedAreas", JSON.stringify(input.blockedAreas));
  }
  if (input.requiredPoints.length > 0) {
    data.append("requiredPoints", JSON.stringify(input.requiredPoints));
  }
  if (input.ignoredConstructionClosureIds.length > 0) {
    data.append("ignoredConstructionClosureIds", JSON.stringify(input.ignoredConstructionClosureIds));
  }
  if (input.plannedStartTime) {
    // new Date(datetimeLocalString) interpretiert den Wert als Browser-Lokalzeit (JS-Spezifikation
    // fuer einen ISO-String ohne Zeitzonen-Suffix) - .toISOString() wandelt das dann eindeutig in
    // UTC um, damit das Backend nicht raten muss, in welcher Zeitzone der Server selbst laeuft
    // (siehe Program.cs ParseOptionalDateTimeOffset).
    data.append("plannedStartTime", new Date(input.plannedStartTime).toISOString());
  }
  data.append("format", format);
  return data;
}

export async function requestRoute(input: RouteFormInput, token: string): Promise<RouteResult> {
  const response = await fetch(`${API_BASE_URL}/route`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` },
    body: buildFormData(input, "json"),
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Route-Anfrage fehlgeschlagen (HTTP ${response.status})`);
  }
  return (await response.json()) as RouteResult;
}

export async function requestRouteGpx(input: RouteFormInput, token: string): Promise<Blob> {
  const response = await fetch(`${API_BASE_URL}/route`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` },
    body: buildFormData(input, "gpx"),
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `GPX-Export fehlgeschlagen (HTTP ${response.status})`);
  }
  return await response.blob();
}

// Einmaliger Abruf fuer den optionalen Ampeln/Stoppschilder-Kartenlayer (siehe MapView) -
// wird vom Frontend gecacht, nicht bei jedem Toggle neu geladen.
export async function requestJunctions(token: string): Promise<Junction[]> {
  const response = await fetch(`${API_BASE_URL}/junctions`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Ampeln/Stoppschilder-Abruf fehlgeschlagen (HTTP ${response.status})`);
  }
  return (await response.json()) as Junction[];
}

// Aktuell aktive, automatisch erkannte Baustellen-Sperrungen (VIZ Berlin, siehe CONCEPT.md
// Abschnitt 6.27) - fuer den Kartenlayer UND die Sidebar-Liste. Wie requestJunctions einmalig
// pro Kartensitzung abgerufen, nicht bei jeder Routenberechnung neu (der serverseitige Cache
// aktualisiert sich ohnehin nur stuendlich).
export async function fetchConstructionClosures(token: string): Promise<ConstructionClosure[]> {
  const response = await fetch(`${API_BASE_URL}/construction-closures`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Baustellen-Abruf fehlgeschlagen (HTTP ${response.status})`);
  }
  return (await response.json()) as ConstructionClosure[];
}

// Registrierungsfehler kommen von POST /auth/register als JSON-Array von Identity-
// Fehlermeldungen zurueck (z.B. Passwortrichtlinie, "E-Mail bereits vergeben"), anders als die
// uebrigen Endpunkte, die reinen Text liefern - daher eigene Fehlerbehandlung hier.
export async function registerUser(email: string, password: string): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/auth/register`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, password }),
  });
  if (!response.ok) {
    const errors = (await response.json().catch(() => null)) as string[] | null;
    throw new Error(errors?.join(" ") || `Registrierung fehlgeschlagen (HTTP ${response.status})`);
  }
}

export async function loginUser(email: string, password: string): Promise<AuthResponse> {
  const response = await fetch(`${API_BASE_URL}/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, password }),
  });
  if (!response.ok) {
    throw new Error(response.status === 401 ? "E-Mail oder Passwort ist falsch." : `Login fehlgeschlagen (HTTP ${response.status})`);
  }
  return (await response.json()) as AuthResponse;
}

// Validiert ein gespeichertes Token gegen das Backend (z.B. beim Laden der App) und liefert die
// zugehoerige E-Mail - null bei abgelaufenem/ungueltigem Token, statt eines Fehlers, da das der
// normale Fall bei einem alten localStorage-Token ist, kein wirklicher Fehlerzustand.
export async function fetchCurrentUser(token: string): Promise<string | null> {
  const response = await fetch(`${API_BASE_URL}/auth/me`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) return null;
  const data = (await response.json()) as { email: string };
  return data.email;
}

export async function buildWorkoutFitFile(blocks: WorkoutBlockSpec[], token: string): Promise<File> {
  const response = await fetch(`${API_BASE_URL}/workout/build`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify(blocks),
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Workout-Erzeugung fehlgeschlagen (HTTP ${response.status})`);
  }
  const blob = await response.blob();
  return new File([blob], "generated-workout.fit", { type: "application/octet-stream" });
}

// Gespeichertes Fahrerprofil (FTP/Gewicht/Sprint-Watt) - siehe GET/PUT /profile (Backend).
// null bei GET = noch kein Profil gespeichert (kein Fehlerzustand, z.B. erster Login).
export interface RiderProfileDto {
  ftpWatts: number;
  weightKg: number;
  sprintAvgWatts: number;
}

export async function fetchProfile(token: string): Promise<RiderProfileDto | null> {
  const response = await fetch(`${API_BASE_URL}/profile`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error(`Profil-Abruf fehlgeschlagen (HTTP ${response.status})`);
  return (await response.json()) as RiderProfileDto;
}

export async function saveProfile(token: string, profile: RiderProfileDto): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/profile`, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify(profile),
  });
  if (!response.ok) throw new Error(`Profil-Speichern fehlgeschlagen (HTTP ${response.status})`);
}
