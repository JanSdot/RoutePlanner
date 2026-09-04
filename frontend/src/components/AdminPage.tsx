import { useEffect, useState } from "react";
import { decideUser, fetchPendingUsers } from "../api";
import type { PendingUser } from "../types";

interface AdminPageProps {
  authToken: string;
}

// Nur erreichbar, wenn App.tsx anhand von /auth/me.isAdmin einen Admin-Tab anzeigt - das
// Backend (Program.cs /admin/users/*) prueft den Admin-Status ohnehin nochmal selbst, dieser
// Tab ist also kein Sicherheitsmechanismus, nur die Bedienoberflaeche dafuer.
export function AdminPage({ authToken }: AdminPageProps) {
  const [pendingUsers, setPendingUsers] = useState<PendingUser[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    fetchPendingUsers(authToken)
      .then(setPendingUsers)
      .catch((err) => setError(err instanceof Error ? err.message : String(err)));
  }, [authToken]);

  async function handleDecide(userId: string, decision: "approve" | "reject") {
    setError(null);
    try {
      await decideUser(authToken, userId, decision);
      setPendingUsers((prev) => prev.filter((u) => u.id !== userId));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  return (
    <div className="page-view">
      <h2>Nutzerfreigabe</h2>
      {error && <p className="error">{error}</p>}
      <fieldset>
        <legend>Wartende Konten</legend>
        {pendingUsers.length === 0 && <p className="hint">Keine offenen Registrierungen.</p>}
        <ul className="point-list">
          {pendingUsers.map((user) => (
            <li key={user.id}>
              {user.email}
              <span>
                <button type="button" onClick={() => handleDecide(user.id, "approve")}>
                  Freigeben
                </button>
                <button type="button" onClick={() => handleDecide(user.id, "reject")}>
                  Ablehnen
                </button>
              </span>
            </li>
          ))}
        </ul>
      </fieldset>
    </div>
  );
}
