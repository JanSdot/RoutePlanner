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
  // Wie surfaceSegments, aber ueber das "smoothness"-Tag - deckt rauen, aber befestigten
  // Belag ab (z.B. rissiger alter Asphalt), siehe MapView ROUGH_SURFACE_WARNING_COLOR.
  smoothnessSegments: SurfaceSegment[];
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

// Sperrgrad einer automatisch erkannten Baustelle (VIZ-Berlin-Feed) - siehe ClosureSeverity
// (Backend). "keine Sperrung" taucht hier nie auf, die wird schon serverseitig herausgefiltert.
export type ClosureSeverity = "Full" | "Directional";

// Eine aktuell aktive, automatisch erkannte Baustellen-Sperrung - siehe ConstructionClosure
// (Backend, CONCEPT.md Abschnitt 6.27). Geometry ist entweder ein einzelner Punkt oder eine
// Punktfolge entlang der betroffenen Straße.
export interface ConstructionClosure {
  id: string;
  street: string;
  geometry: GeoPoint[];
  severity: ClosureSeverity;
  validFrom?: string | null;
  validTo?: string | null;
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
  // Begrenzt rauen, aber befestigten Belag (surface=asphalt/... + smoothness=bad) - bewusst
  // GETRENNT von maxTotalUnpavedMeters, siehe RouteRequest.MaxTotalRoughMeters (Backend).
  maxTotalRoughMeters?: number | null;
  maxDisruptiveJunctions?: number | null;
  // Wirkungslos, wenn keines der drei Limits oben gesetzt ist - siehe RouteRequest.
  // MaxRouteVariantAttempts (Backend).
  maxRouteVariantAttempts?: number | null;
  blockedAreas: BlockedArea[];
  // Punkte, durch die die Route zwingend fuehren soll - siehe RouteRequest.RequiredPoints
  // (Backend).
  requiredPoints: GeoPoint[];
  // IDs automatisch erkannter Baustellen (siehe ConstructionClosure), die der Nutzer bewusst
  // fuer DIESE Route ignorieren moechte - siehe RouteRequest.ConstructionClosures (Backend).
  ignoredConstructionClosureIds: string[];
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

// Vereine (CONCEPT.md Phase-4-Backlog "Mehrbenutzerfähigkeit/Auth/Vereine", Stufe 2) - siehe
// GET /clubs (Backend).
export interface Club {
  id: string;
  name: string;
  memberCount: number;
}

// Eigene Vereinsmitgliedschaft - siehe GET /clubs/mine (Backend). null = kein Verein.
export interface ClubMembership {
  clubId: string;
  clubName: string;
  status: "Pending" | "Approved";
  isAdmin: boolean;
}

// Eine offene Beitrittsanfrage - nur fuer Verantwortliche sichtbar, siehe
// GET /clubs/{clubId}/members/pending (Backend).
export interface PendingMember {
  membershipId: string;
  email: string;
  requestedAt: string;
}

// Eine dauerhaft gespeicherte Sperrung (persoenlich ODER Verein) - Ergaenzung zu BlockedArea
// (die weiterhin rein Request-lokal/temporaer bleibt), siehe CONCEPT.md Phase-4-Backlog
// "Mehrbenutzerfähigkeit/Auth/Vereine", Stufe 3. "scope" kommt NICHT vom Backend (SegmentLockDto
// kennt nur "status"), sondern wird beim Laden in App.tsx anhand des Endpunkts angereichert
// (persoenlich vs. Club), fuers Kartenlayer-Rendering.
export interface SegmentLock {
  id: string;
  lat: number;
  lon: number;
  radiusMeters: number;
  status: "Active" | "Pending" | "Rejected";
  createdAt: string;
  scope: "personal" | "club";
}
