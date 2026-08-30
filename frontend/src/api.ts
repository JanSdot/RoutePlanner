import type { RouteFormInput, RouteResult } from "./types";

const API_BASE_URL = "http://localhost:5080";

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
