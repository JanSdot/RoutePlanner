import type { RouteFormInput, RouteResult, WorkoutBlockSpec } from "./types";

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
  data.append("format", format);
  return data;
}

export async function requestRoute(input: RouteFormInput): Promise<RouteResult> {
  const response = await fetch(`${API_BASE_URL}/route`, {
    method: "POST",
    body: buildFormData(input, "json"),
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Route-Anfrage fehlgeschlagen (HTTP ${response.status})`);
  }
  return (await response.json()) as RouteResult;
}

export async function requestRouteGpx(input: RouteFormInput): Promise<Blob> {
  const response = await fetch(`${API_BASE_URL}/route`, {
    method: "POST",
    body: buildFormData(input, "gpx"),
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `GPX-Export fehlgeschlagen (HTTP ${response.status})`);
  }
  return await response.blob();
}

export async function buildWorkoutFitFile(blocks: WorkoutBlockSpec[]): Promise<File> {
  const response = await fetch(`${API_BASE_URL}/workout/build`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(blocks),
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Workout-Erzeugung fehlgeschlagen (HTTP ${response.status})`);
  }
  const blob = await response.blob();
  return new File([blob], "generated-workout.fit", { type: "application/octet-stream" });
}
