import { useEffect, useState } from "react";
import {
  decideClub,
  decideUser,
  deleteUser,
  fetchAdminClubMembers,
  fetchAllClubsForAdmin,
  fetchAllUsers,
  fetchPendingClubs,
  fetchPendingUsers,
  setClubMemberAdmin,
  setUserLocked,
} from "../api";
import type { AdminClub, AdminClubMember, AdminUser, PendingClub, PendingUser } from "../types";

interface AdminPageProps {
  authToken: string;
}

const USER_STATUS_LABELS: Record<AdminUser["status"], string> = {
  PendingApproval: "Wartet auf Freigabe",
  Suspended: "Gesperrt",
  Active: "Aktiv",
};

// Nur erreichbar, wenn App.tsx anhand von /auth/me.isAdmin einen Admin-Tab anzeigt - das
// Backend (Program.cs /admin/*) prueft den Admin-Status ohnehin nochmal selbst, dieser Tab ist
// also kein Sicherheitsmechanismus, nur die Bedienoberflaeche dafuer.
export function AdminPage({ authToken }: AdminPageProps) {
  const [pendingUsers, setPendingUsers] = useState<PendingUser[]>([]);
  const [allUsers, setAllUsers] = useState<AdminUser[]>([]);
  const [pendingClubs, setPendingClubs] = useState<PendingClub[]>([]);
  const [allClubs, setAllClubs] = useState<AdminClub[]>([]);
  const [selectedClubId, setSelectedClubId] = useState<string | null>(null);
  const [clubMembers, setClubMembers] = useState<AdminClubMember[]>([]);
  const [error, setError] = useState<string | null>(null);

  function reportError(err: unknown) {
    setError(err instanceof Error ? err.message : String(err));
  }

  function reloadUsers() {
    fetchPendingUsers(authToken).then(setPendingUsers).catch(reportError);
    fetchAllUsers(authToken).then(setAllUsers).catch(reportError);
  }

  function reloadClubs() {
    fetchPendingClubs(authToken).then(setPendingClubs).catch(reportError);
    fetchAllClubsForAdmin(authToken).then(setAllClubs).catch(reportError);
  }

  useEffect(() => {
    reloadUsers();
    reloadClubs();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [authToken]);

  useEffect(() => {
    if (!selectedClubId) {
      setClubMembers([]);
      return;
    }
    fetchAdminClubMembers(authToken, selectedClubId).then(setClubMembers).catch(reportError);
  }, [authToken, selectedClubId]);

  async function handleDecideUser(userId: string, decision: "approve" | "reject") {
    setError(null);
    try {
      await decideUser(authToken, userId, decision);
      reloadUsers();
    } catch (err) {
      reportError(err);
    }
  }

  async function handleSetLocked(userId: string, locked: boolean) {
    setError(null);
    try {
      await setUserLocked(authToken, userId, locked);
      reloadUsers();
    } catch (err) {
      reportError(err);
    }
  }

  async function handleDeleteUser(userId: string, email: string) {
    if (!window.confirm(`Konto ${email} unwiderruflich löschen?`)) return;
    setError(null);
    try {
      await deleteUser(authToken, userId);
      reloadUsers();
    } catch (err) {
      reportError(err);
    }
  }

  async function handleDecideClub(clubId: string, decision: "approve" | "reject") {
    if (decision === "reject" && !window.confirm("Verein ablehnen? Der Verein und seine Mitgliedschaften werden dabei gelöscht.")) return;
    setError(null);
    try {
      await decideClub(authToken, clubId, decision);
      reloadClubs();
      if (decision === "reject" && selectedClubId === clubId) setSelectedClubId(null);
    } catch (err) {
      reportError(err);
    }
  }

  async function handleSetClubAdmin(membershipId: string, isAdmin: boolean) {
    if (!selectedClubId) return;
    setError(null);
    try {
      await setClubMemberAdmin(authToken, selectedClubId, membershipId, isAdmin);
      setClubMembers(await fetchAdminClubMembers(authToken, selectedClubId));
    } catch (err) {
      reportError(err);
    }
  }

  return (
    <div className="page-view">
      <h2>Verwaltung</h2>
      {error && <p className="error">{error}</p>}

      <fieldset>
        <legend>Wartende Registrierungen</legend>
        {pendingUsers.length === 0 && <p className="hint">Keine offenen Registrierungen.</p>}
        <ul className="point-list">
          {pendingUsers.map((user) => (
            <li key={user.id}>
              {user.email}
              <span>
                <button type="button" onClick={() => handleDecideUser(user.id, "approve")}>
                  Freigeben
                </button>
                <button type="button" onClick={() => handleDecideUser(user.id, "reject")}>
                  Ablehnen
                </button>
              </span>
            </li>
          ))}
        </ul>
      </fieldset>

      <fieldset>
        <legend>Alle Nutzer</legend>
        <ul className="point-list">
          {allUsers.map((user) => (
            <li key={user.id}>
              {user.email} ({USER_STATUS_LABELS[user.status]})
              {!user.isSelf && (
                <span>
                  {user.status === "Suspended" ? (
                    <button type="button" onClick={() => handleSetLocked(user.id, false)}>
                      Entsperren
                    </button>
                  ) : user.status === "Active" ? (
                    <button type="button" onClick={() => handleSetLocked(user.id, true)}>
                      Sperren
                    </button>
                  ) : null}
                  <button type="button" onClick={() => handleDeleteUser(user.id, user.email)}>
                    Löschen
                  </button>
                </span>
              )}
            </li>
          ))}
        </ul>
      </fieldset>

      <fieldset>
        <legend>Wartende Vereine</legend>
        {pendingClubs.length === 0 && <p className="hint">Keine offenen Vereinsanfragen.</p>}
        <ul className="point-list">
          {pendingClubs.map((club) => (
            <li key={club.id}>
              {club.name} (Gründer: {club.creatorEmail})
              <span>
                <button type="button" onClick={() => handleDecideClub(club.id, "approve")}>
                  Freigeben
                </button>
                <button type="button" onClick={() => handleDecideClub(club.id, "reject")}>
                  Ablehnen
                </button>
              </span>
            </li>
          ))}
        </ul>
      </fieldset>

      <fieldset>
        <legend>Vereine verwalten</legend>
        <ul className="point-list">
          {allClubs.map((club) => (
            <li key={club.id}>
              <button
                type="button"
                className={selectedClubId === club.id ? "active" : ""}
                onClick={() => setSelectedClubId(selectedClubId === club.id ? null : club.id)}
              >
                {club.name} ({club.status === "Pending" ? "wartet" : `${club.memberCount} Mitglieder`})
              </button>
            </li>
          ))}
        </ul>

        {selectedClubId && (
          <ul className="point-list">
            {clubMembers.map((member) => (
              <li key={member.membershipId}>
                {member.email} ({member.status}
                {member.isAdmin ? ", Verantwortlicher" : ""})
                {member.status === "Approved" && (
                  <span>
                    {member.isAdmin ? (
                      <button type="button" onClick={() => handleSetClubAdmin(member.membershipId, false)}>
                        Verantwortung entziehen
                      </button>
                    ) : (
                      <button type="button" onClick={() => handleSetClubAdmin(member.membershipId, true)}>
                        Zum Verantwortlichen machen
                      </button>
                    )}
                  </span>
                )}
              </li>
            ))}
          </ul>
        )}
      </fieldset>
    </div>
  );
}
