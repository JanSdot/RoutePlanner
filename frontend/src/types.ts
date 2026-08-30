export interface GeoPoint {
  lat: number;
  lon: number;
  elevation?: number | null;
}

export interface RouteWarning {
  message: string;
  location?: GeoPoint | null;
}

export interface RouteSegment {
  label: string;
  geometry: GeoPoint[];
}

export interface SurfaceSegment {
  surface: string;
  geometry: GeoPoint[];
}

// Ampel/Stoppschild fuer den optionalen Kartenlayer - siehe Junction (Backend).
export interface Junction {
  point: GeoPoint;
  type: "TrafficSignal" | "Stop";
}

// Die fuer die Zeitschaetzung tatsaechlich genutzten Windbedingungen - siehe
// RouteResult.Wind (Backend), null wenn kein plannedStartTime gesetzt war oder keine
// Vorhersage verfuegbar. WindFromDirectionDegrees folgt meteorologischer Konvention (Richtung,
// AUS der der Wind weht).
export interface WindConditions {
  windSpeedMps: number;
  windFromDirectionDegrees: number;
}

export interface RouteResult {
  geometry: GeoPoint[];
  totalDistanceMeters: number;
  estimatedTotalTime: string; // .NET TimeSpan "c" format: "hh:mm:ss.fffffff"
  warnings: RouteWarning[];
  segments: RouteSegment[];
  surfaceSegments: SurfaceSegment[];
  wind?: WindConditions | null;
}

export type SegmentReusePreference = "PreferReuse" | "PreferVariety";

// Ein vom Nutzer auf der Karte markierter Bereich, der bei der Routenberechnung gemieden
// werden soll - siehe RouteRequest.BlockedAreas (Backend).
export interface BlockedArea {
  lat: number;
  lon: number;
  radiusMeters: number;
}

export interface RiderProfileInput {
  ftpWatts: number;
  weightKg: number;
  sprintAvgWatts: number;
}

export interface RouteFormInput {
  rider: RiderProfileInput;
  startLat: number;
  startLon: number;
  maxApproachMinutes: number;
  segmentReuse: SegmentReusePreference;
  allowUTurns: boolean;
  // null/undefined = keine Begrenzung, siehe RouteRequest.MaxUnpavedSegmentMeters (Backend).
  maxUnpavedSegmentMeters?: number | null;
  maxTotalUnpavedMeters?: number | null;
  maxDisruptiveJunctions?: number | null;
  // Wirkungslos, wenn keines der drei Limits oben gesetzt ist - siehe RouteRequest.
  // MaxRouteVariantAttempts (Backend).
  maxRouteVariantAttempts?: number | null;
  blockedAreas: BlockedArea[];
  // Punkte, durch die die Route zwingend fuehren soll - siehe RouteRequest.RequiredPoints
  // (Backend).
  requiredPoints: GeoPoint[];
  // Wert eines <input type="datetime-local">, in der Browser-Lokalzeit (kein Zeitzonen-Wissen
  // im String selbst) - wird in api.ts vor dem Versand in UTC umgewandelt, siehe RouteRequest.
  // PlannedStartTime (Backend). null/leer = keine Windvorhersage.
  plannedStartTime?: string | null;
  fitFile: File;
}

// Sprint wird vom Block-Editor bewusst nicht unterstuetzt (nicht %FTP-basiert, siehe
// FitWorkoutEncoder).
export type EditorZone = "GA1" | "GA2" | "EB" | "SB" | "VO2max";

export interface WorkoutStepSpec {
  zone: EditorZone;
  durationMinutes: number;
}

export interface WorkoutBlockSpec {
  step?: WorkoutStepSpec;
  repeatTimes?: number;
  repeatSteps?: WorkoutStepSpec[];
}

// Siehe POST /auth/login (Backend) - Token ist ein JWT, 30 Tage gueltig.
export interface AuthResponse {
  token: string;
  email: string;
}
