export interface GeoPoint {
  lat: number;
  lon: number;
  elevation?: number | null;
}

export interface RouteWarning {
  message: string;
  location?: GeoPoint | null;
}

export interface RouteResult {
  geometry: GeoPoint[];
  totalDistanceMeters: number;
  estimatedTotalTime: string; // .NET TimeSpan "c" format: "hh:mm:ss.fffffff"
  warnings: RouteWarning[];
}

export type SegmentReusePreference = "PreferReuse" | "PreferVariety";

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
  fitFile: File;
}
