import { useState } from "react";

interface ProfilePageProps {
  ftpWatts: number;
  weightKg: number;
  sprintAvgWatts: number;
  onChange: (profile: { ftpWatts: number; weightKg: number; sprintAvgWatts: number }) => void;
  onSave: () => Promise<void>;
}

// Eigene Seite statt inline in der Routenplanungs-Seitenleiste (Nutzerwunsch) - Werte/State
// bleiben in App.tsx (unveraendert von fetchProfile beim Login geladen, handleSubmit sendet sie
// weiterhin unveraendert mit), diese Komponente ist rein die Eingabe-/Speichern-Ansicht.
export function ProfilePage({ ftpWatts, weightKg, sprintAvgWatts, onChange, onSave }: ProfilePageProps) {
  const [saving, setSaving] = useState(false);
  const [savedJustNow, setSavedJustNow] = useState(false);

  async function handleSave() {
    setSaving(true);
    setSavedJustNow(false);
    try {
      await onSave();
      setSavedJustNow(true);
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="page-view">
      <h2>Profil</h2>
      <p className="hint">
        FTP, Gewicht und Sprint-Ø-Watt fließen in jede Zeitschätzung ein und werden mit jeder
        Routenberechnung ohnehin automatisch gespeichert - hier lassen sie sich unabhängig davon
        pflegen.
      </p>
      <label>
        FTP (Watt)
        <input
          type="number"
          value={ftpWatts}
          onChange={(e) => onChange({ ftpWatts: Number(e.target.value), weightKg, sprintAvgWatts })}
        />
      </label>
      <label>
        Gewicht (kg)
        <input
          type="number"
          value={weightKg}
          onChange={(e) => onChange({ ftpWatts, weightKg: Number(e.target.value), sprintAvgWatts })}
        />
      </label>
      <label>
        Sprint Ø-Watt
        <input
          type="number"
          value={sprintAvgWatts}
          onChange={(e) => onChange({ ftpWatts, weightKg, sprintAvgWatts: Number(e.target.value) })}
        />
      </label>
      <button type="button" onClick={handleSave} disabled={saving}>
        {saving ? "Speichert…" : "Speichern"}
      </button>
      {savedJustNow && <p className="hint">Gespeichert.</p>}
    </div>
  );
}
