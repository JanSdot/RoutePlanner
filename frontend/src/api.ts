import type {
  AdminClub,
  AdminClubMember,
  AdminUser,
  AuthResponse,
  Club,
  ClubMembership,
  ConstructionClosure,
  Junction,
  PendingClub,
  PendingMember,
  PendingUser,
  RouteFormInput,
  RouteResult,
  SegmentLock,
  WorkoutBlockSpec,
} from "./types";

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
  data.append("showAlternatives", String(input.showAlternatives));
  if (input.seed != null) {
    data.append("seed", String(input.seed));
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
    // "pending_approval"/"suspended" kommen von Program.cs /auth/login (siehe /admin/users/*) -
    // eigene Fehlertexte statt des generischen 401-Falls, damit klar ist, dass Adresse/Passwort
    // stimmen und es stattdessen an der Konto-Freigabe liegt.
    if (response.status === 403) {
      const body = (await response.json().catch(() => null)) as { error?: string } | null;
      if (body?.error === "pending_approval") {
        throw new Error("Dein Konto wartet noch auf Freigabe durch einen Administrator.");
      }
      if (body?.error === "suspended") {
        throw new Error("Dein Konto wurde von einem Administrator gesperrt.");
      }
    }
    throw new Error(response.status === 401 ? "E-Mail oder Passwort ist falsch." : `Login fehlgeschlagen (HTTP ${response.status})`);
  }
  return (await response.json()) as AuthResponse;
}

// Validiert ein gespeichertes Token gegen das Backend (z.B. beim Laden der App) und liefert
// E-Mail + Admin-Status - null bei abgelaufenem/ungueltigem Token, statt eines Fehlers, da das
// der normale Fall bei einem alten localStorage-Token ist, kein wirklicher Fehlerzustand.
export async function fetchCurrentUser(token: string): Promise<{ email: string; isAdmin: boolean } | null> {
  const response = await fetch(`${API_BASE_URL}/auth/me`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) return null;
  return (await response.json()) as { email: string; isAdmin: boolean };
}

// Konten, die auf manuelle Freigabe warten (siehe GET /admin/users/pending) - nur fuer
// Administratoren sichtbar, das Backend liefert sonst 403.
export async function fetchPendingUsers(token: string): Promise<PendingUser[]> {
  const response = await fetch(`${API_BASE_URL}/admin/users/pending`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Abruf der offenen Konten fehlgeschlagen (HTTP ${response.status})`);
  }
  return (await response.json()) as PendingUser[];
}

export async function decideUser(token: string, userId: string, decision: "approve" | "reject"): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/admin/users/${userId}/${decision}`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Aktion fehlgeschlagen (HTTP ${response.status})`);
  }
}

// Alle Konten (nicht nur wartende) - siehe GET /admin/users (Backend).
export async function fetchAllUsers(token: string): Promise<AdminUser[]> {
  const response = await fetch(`${API_BASE_URL}/admin/users`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Abruf der Nutzerliste fehlgeschlagen (HTTP ${response.status})`);
  }
  return (await response.json()) as AdminUser[];
}

export async function setUserLocked(token: string, userId: string, locked: boolean): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/admin/users/${userId}/${locked ? "lock" : "unlock"}`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Aktion fehlgeschlagen (HTTP ${response.status})`);
  }
}

export async function deleteUser(token: string, userId: string): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/admin/users/${userId}`, {
    method: "DELETE",
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Löschen fehlgeschlagen (HTTP ${response.status})`);
  }
}

// Vereine, die auf Plattform-Freigabe warten - siehe GET /admin/clubs/pending (Backend).
export async function fetchPendingClubs(token: string): Promise<PendingClub[]> {
  const response = await fetch(`${API_BASE_URL}/admin/clubs/pending`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Abruf der offenen Vereine fehlgeschlagen (HTTP ${response.status})`);
  }
  return (await response.json()) as PendingClub[];
}

export async function decideClub(token: string, clubId: string, decision: "approve" | "reject"): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/admin/clubs/${clubId}/${decision}`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Aktion fehlgeschlagen (HTTP ${response.status})`);
  }
}

// Alle Vereine (nicht nur wartende) - siehe GET /admin/clubs (Backend).
export async function fetchAllClubsForAdmin(token: string): Promise<AdminClub[]> {
  const response = await fetch(`${API_BASE_URL}/admin/clubs`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Abruf der Vereinsliste fehlgeschlagen (HTTP ${response.status})`);
  }
  return (await response.json()) as AdminClub[];
}

// Alle Mitgliedschaften eines Vereins (nicht nur Pending) - siehe
// GET /admin/clubs/{clubId}/members (Backend).
export async function fetchAdminClubMembers(token: string, clubId: string): Promise<AdminClubMember[]> {
  const response = await fetch(`${API_BASE_URL}/admin/clubs/${clubId}/members`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Abruf der Vereinsmitglieder fehlgeschlagen (HTTP ${response.status})`);
  }
  return (await response.json()) as AdminClubMember[];
}

export async function setClubMemberAdmin(token: string, clubId: string, membershipId: string, isAdmin: boolean): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/admin/clubs/${clubId}/members/${membershipId}/set-admin`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify({ isAdmin }),
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Aktion fehlgeschlagen (HTTP ${response.status})`);
  }
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

// Vereine (CONCEPT.md Phase-4-Backlog "Mehrbenutzerfähigkeit/Auth/Vereine", Stufe 2).

export async function fetchClubs(token: string): Promise<Club[]> {
  const response = await fetch(`${API_BASE_URL}/clubs`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error(`Vereins-Liste fehlgeschlagen (HTTP ${response.status})`);
  return (await response.json()) as Club[];
}

export async function fetchMyClubMembership(token: string): Promise<ClubMembership | null> {
  const response = await fetch(`${API_BASE_URL}/clubs/mine`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error(`Vereins-Mitgliedschaft fehlgeschlagen (HTTP ${response.status})`);
  return (await response.json()) as ClubMembership | null;
}

export async function createClub(token: string, name: string): Promise<Club> {
  const response = await fetch(`${API_BASE_URL}/clubs`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify({ name }),
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Verein anlegen fehlgeschlagen (HTTP ${response.status})`);
  }
  return (await response.json()) as Club;
}

export async function joinClub(token: string, clubId: string): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/clubs/${clubId}/join`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Beitritt fehlgeschlagen (HTTP ${response.status})`);
  }
}

export async function leaveClub(token: string, clubId: string): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/clubs/${clubId}/leave`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error(`Verein verlassen fehlgeschlagen (HTTP ${response.status})`);
}

export async function fetchPendingClubMembers(token: string, clubId: string): Promise<PendingMember[]> {
  const response = await fetch(`${API_BASE_URL}/clubs/${clubId}/members/pending`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error(`Beitrittsanfragen fehlgeschlagen (HTTP ${response.status})`);
  return (await response.json()) as PendingMember[];
}

export async function decideClubMember(
  token: string,
  clubId: string,
  membershipId: string,
  decision: "approve" | "reject",
): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/clubs/${clubId}/members/${membershipId}/${decision}`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error(`Entscheidung fehlgeschlagen (HTTP ${response.status})`);
}

// Persistierte Sperr-Bereiche (Stufe 3) - siehe SegmentLock (types.ts). "scope" wird hier
// clientseitig anhand des jeweiligen Endpunkts ergänzt, da das Backend es nicht mitliefert.

export async function fetchMySegmentLocks(token: string): Promise<SegmentLock[]> {
  const response = await fetch(`${API_BASE_URL}/segment-locks/mine`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error(`Eigene Sperren fehlgeschlagen (HTTP ${response.status})`);
  const locks = (await response.json()) as Omit<SegmentLock, "scope">[];
  return locks.map((l) => ({ ...l, scope: "personal" as const }));
}

export async function fetchActiveClubSegmentLocks(token: string, clubId: string): Promise<SegmentLock[]> {
  const response = await fetch(`${API_BASE_URL}/clubs/${clubId}/segment-locks/active`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error(`Vereins-Sperren fehlgeschlagen (HTTP ${response.status})`);
  const locks = (await response.json()) as Omit<SegmentLock, "scope">[];
  return locks.map((l) => ({ ...l, scope: "club" as const }));
}

export async function fetchPendingClubSegmentLocks(token: string, clubId: string): Promise<SegmentLock[]> {
  const response = await fetch(`${API_BASE_URL}/clubs/${clubId}/segment-locks/pending`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error(`Offene Sperr-Vorschläge fehlgeschlagen (HTTP ${response.status})`);
  const locks = (await response.json()) as Omit<SegmentLock, "scope">[];
  return locks.map((l) => ({ ...l, scope: "club" as const }));
}

export async function createPersonalSegmentLock(token: string, lat: number, lon: number, radiusMeters: number): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/segment-locks/personal`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify({ lat, lon, radiusMeters }),
  });
  if (!response.ok) throw new Error(`Dauerhafte Sperre fehlgeschlagen (HTTP ${response.status})`);
}

export async function proposeClubSegmentLock(token: string, lat: number, lon: number, radiusMeters: number): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/segment-locks/club`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify({ lat, lon, radiusMeters }),
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Vereins-Sperre vorschlagen fehlgeschlagen (HTTP ${response.status})`);
  }
}

export async function deleteSegmentLock(token: string, id: string): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/segment-locks/${id}`, {
    method: "DELETE",
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error(`Löschen fehlgeschlagen (HTTP ${response.status})`);
}

export async function decideSegmentLock(token: string, id: string, decision: "approve" | "reject"): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/segment-locks/${id}/${decision}`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) throw new Error(`Entscheidung fehlgeschlagen (HTTP ${response.status})`);
}
