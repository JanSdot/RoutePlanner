import { useState } from "react";

interface AuthPanelProps {
  email: string | null;
  loading: boolean;
  error: string | null;
  onLogin: (email: string, password: string) => void;
  onRegister: (email: string, password: string) => void;
  onLogout: () => void;
}

// Kein hartes Login-Gate: das Konten-Feature (CONCEPT.md Phase-4-Backlog) steckt noch in Stufe 1
// (nur Login selbst) - solange Stufe 3 (persistierte, pro-Nutzer geltende Sperr-Bereiche) nicht
// existiert, gibt es nichts, das ein eingeloggter Zustand tatsaechlich freischalten wuerde. Die
// restliche App bleibt daher fuer alle nutzbar, dieses Panel ist rein optional.
export function AuthPanel({ email, loading, error, onLogin, onRegister, onLogout }: AuthPanelProps) {
  const [inputEmail, setInputEmail] = useState("");
  const [inputPassword, setInputPassword] = useState("");

  if (email) {
    return (
      <fieldset className="auth-panel">
        <legend>Konto</legend>
        <p>Angemeldet als {email}</p>
        <button type="button" onClick={onLogout}>
          Abmelden
        </button>
      </fieldset>
    );
  }

  return (
    <fieldset className="auth-panel">
      <legend>Konto</legend>
      <label>
        E-Mail
        <input
          type="email"
          value={inputEmail}
          onChange={(e) => setInputEmail(e.target.value)}
          autoComplete="email"
        />
      </label>
      <label>
        Passwort
        <input
          type="password"
          value={inputPassword}
          onChange={(e) => setInputPassword(e.target.value)}
          autoComplete="current-password"
        />
      </label>
      {error && <p className="error">{error}</p>}
      <div className="auth-panel-actions">
        <button type="button" disabled={loading} onClick={() => onLogin(inputEmail, inputPassword)}>
          Anmelden
        </button>
        <button type="button" disabled={loading} onClick={() => onRegister(inputEmail, inputPassword)}>
          Registrieren
        </button>
      </div>
    </fieldset>
  );
}
