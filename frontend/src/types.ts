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

export interface RouteResult {
  geometry: GeoPoint[];
  totalDistanceMeters: number;
  estimatedTotalTime: string; // .NET TimeSpan "c" format: "hh:mm:ss.fffffff"
  warnings: RouteWarning[];
  segments: RouteSegment[];
  surfaceSegments: SurfaceSegment[];
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
