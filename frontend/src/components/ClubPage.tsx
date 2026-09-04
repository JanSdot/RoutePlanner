import { useEffect, useState } from "react";
import {
  createClub,
  decideClubMember,
  decideSegmentLock,
  fetchClubs,
  fetchMyClubMembership,
  fetchPendingClubMembers,
  fetchPendingClubSegmentLocks,
  joinClub,
  leaveClub,
} from "../api";
import type { Club, ClubMembership, PendingMember, SegmentLock } from "../types";

interface ClubPageProps {
  authToken: string;
  // App.tsx haelt die eigene Mitgliedschaft (fuer den Kartenklick-Popup/Kartenlayer, siehe
  // MapView isApprovedClubMember) - diese Seite meldet Aenderungen zurueck, statt sie doppelt
  // selbst zu laden.
  membership: ClubMembership | null;
  onMembershipChange: (membership: ClubMembership | null) => void;
}

// Vereine (CONCEPT.md Phase-4-Backlog "Mehrbenutzerfähigkeit/Auth/Vereine", Stufe 2) - Beitritt
// braucht die Freigabe eines Verantwortlichen, ein Verein kann mehrere Verantwortliche haben.
export function ClubPage({ authToken, membership, onMembershipChange }: ClubPageProps) {
  const [clubs, setClubs] = useState<Club[]>([]);
  const [newClubName, setNewClubName] = useState("");
  const [pendingMembers, setPendingMembers] = useState<PendingMember[]>([]);
  const [pendingLocks, setPendingLocks] = useState<SegmentLock[]>([]);
  const [error, setError] = useState<string | null>(null);

  async function reloadMembership() {
    const m = await fetchMyClubMembership(authToken);
    onMembershipChange(m);
  }

  useEffect(() => {
    if (!membership) {
      fetchClubs(authToken).then(setClubs).catch(() => {});
    }
  }, [authToken, membership]);

  useEffect(() => {
    if (!membership || membership.status !== "Approved") return;
    fetchPendingClubMembers(authToken, membership.clubId).then(setPendingMembers).catch(() => {});
    fetchPendingClubSegmentLocks(authToken, membership.clubId).then(setPendingLocks).catch(() => {});
  }, [authToken, membership]);

  async function handleCreateClub() {
    setError(null);
    try {
      await createClub(authToken, newClubName);
      setNewClubName("");
      await reloadMembership();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function handleJoin(clubId: string) {
    setError(null);
    try {
      await joinClub(authToken, clubId);
      await reloadMembership();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function handleLeave() {
    if (!membership) return;
    await leaveClub(authToken, membership.clubId);
    await reloadMembership();
  }

  async function handleDecideMember(membershipId: string, decision: "approve" | "reject") {
    if (!membership) return;
    await decideClubMember(authToken, membership.clubId, membershipId, decision);
    setPendingMembers((prev) => prev.filter((m) => m.membershipId !== membershipId));
  }

  async function handleDecideLock(id: string, decision: "approve" | "reject") {
    await decideSegmentLock(authToken, id, decision);
    setPendingLocks((prev) => prev.filter((l) => l.id !== id));
  }

  return (
    <div className="page-view">
      <h2>Verein</h2>
      {error && <p className="error">{error}</p>}

      {!membership && (
        <>
          <fieldset>
            <legend>Verein gründen</legend>
            <label>
              Name
              <input type="text" value={newClubName} onChange={(e) => setNewClubName(e.target.value)} />
            </label>
            <button type="button" onClick={handleCreateClub} disabled={!newClubName.trim()}>
              Gründen
            </button>
          </fieldset>

          <fieldset>
            <legend>Verein beitreten</legend>
            {clubs.length === 0 && <p className="hint">Noch keine Vereine vorhanden.</p>}
            <ul className="point-list">
              {clubs.map((club) => (
                <li key={club.id}>
                  {club.name} ({club.memberCount} Mitglieder)
                  <button type="button" onClick={() => handleJoin(club.id)}>
                    Beitreten
                  </button>
                </li>
              ))}
            </ul>
          </fieldset>
        </>
      )}

      {membership?.status === "Pending" && (
        <p className="hint">
          Deine Anfrage bei <strong>{membership.clubName}</strong> wartet auf Freigabe durch einen
          Verantwortlichen.{" "}
          <button type="button" onClick={handleLeave}>
            Zurückziehen
          </button>
        </p>
      )}

      {membership?.status === "Approved" && (
        <>
          <p>
            Mitglied bei <strong>{membership.clubName}</strong>
            {membership.isAdmin && " (Verantwortlicher)"}
            {" - "}
            <button type="button" onClick={handleLeave}>
              Verlassen
            </button>
          </p>

          {membership.clubStatus === "Pending" && (
            <p className="hint">Dieser Verein wartet noch auf die Freigabe durch die Plattform-Administration.</p>
          )}

          <fieldset>
            <legend>Offene Sperr-Vorschläge für den Verein</legend>
            {pendingLocks.length === 0 && <p className="hint">Keine offenen Vorschläge.</p>}
            <ul className="point-list">
              {pendingLocks.map((lock) => (
                <li key={lock.id}>
                  {lock.lat.toFixed(5)}, {lock.lon.toFixed(5)} ({lock.radiusMeters} m)
                  {membership.isAdmin && (
                    <span>
                      <button type="button" onClick={() => handleDecideLock(lock.id, "approve")}>
                        Freigeben
                      </button>
                      <button type="button" onClick={() => handleDecideLock(lock.id, "reject")}>
                        Ablehnen
                      </button>
                    </span>
                  )}
                </li>
              ))}
            </ul>
          </fieldset>

          {membership.isAdmin && (
            <fieldset>
              <legend>Offene Beitrittsanfragen</legend>
              {pendingMembers.length === 0 && <p className="hint">Keine offenen Anfragen.</p>}
              <ul className="point-list">
                {pendingMembers.map((m) => (
                  <li key={m.membershipId}>
                    {m.email}
                    <span>
                      <button type="button" onClick={() => handleDecideMember(m.membershipId, "approve")}>
                        Freigeben
                      </button>
                      <button type="button" onClick={() => handleDecideMember(m.membershipId, "reject")}>
                        Ablehnen
                      </button>
                    </span>
                  </li>
                ))}
              </ul>
            </fieldset>
          )}
        </>
      )}
    </div>
  );
}
