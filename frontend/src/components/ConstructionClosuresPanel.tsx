import { useState } from "react";
import type { ConstructionClosure } from "../types";

interface ConstructionClosuresPanelProps {
  closures: ConstructionClosure[];
  ignoredClosureIds: Set<string>;
  onToggleIgnored: (id: string) => void;
}

// Wie HelpPanel (map-icon-button + modal-overlay/-panel), aber Daten/Zustand bleiben in App.tsx
// (anders als beim rein statischen HelpPanel) - dieser Button/das Modal sind nur die Anzeige.
// Vom Nutzer gewünscht: die Baustellen-Liste stand vorher in der linken Seitenleiste, sollte
// aber in ein eigenes Fenster wandern, nicht dort verbleiben.
export function ConstructionClosuresPanel({ closures, ignoredClosureIds, onToggleIgnored }: ConstructionClosuresPanelProps) {
  const [open, setOpen] = useState(false);

  // Kein Button, wenn (noch) keine Baustellen bekannt sind - wie zuvor die bedingt gerenderte
  // Seitenleisten-Sektion.
  if (closures.length === 0) return null;

  return (
    <>
      <button
        type="button"
        className="map-icon-button construction-button"
        onClick={() => setOpen(true)}
        aria-label="Baustellen anzeigen"
        title="Baustellen"
      >
        🚧
      </button>

      {open && (
        <div className="modal-overlay" onClick={() => setOpen(false)}>
          <div className="modal-panel" onClick={(e) => e.stopPropagation()}>
            <div className="modal-panel-header">
              <h2>Baustellen in der Nähe (Berlin)</h2>
              <button type="button" className="modal-close" onClick={() => setOpen(false)} aria-label="Schließen">
                ×
              </button>
            </div>

            <p className="hint">
              Automatisch erkannt (VIZ Berlin, stündlich aktualisiert) - werden bei der
              Routenberechnung standardmäßig gemieden. Nicht jede Kleinbaustelle ist erfasst,
              und einzelne Einträge können veraltet sein - bei Bedarf ignorieren.
            </p>
            <ul className="point-list">
              {closures.map((closure) => {
                const ignored = ignoredClosureIds.has(closure.id);
                return (
                  <li key={closure.id} className={ignored ? "ignored" : undefined}>
                    {closure.street || "Unbenannte Straße"}
                    {" "}({closure.severity === "Full" ? "Vollsperrung" : "Fahrtrichtungssperrung"})
                    <button type="button" onClick={() => onToggleIgnored(closure.id)}>
                      {ignored ? "Wieder berücksichtigen" : "Ignorieren für diese Route"}
                    </button>
                  </li>
                );
              })}
            </ul>
            <p className="hint">
              Baustellen-Daten:{" "}
              <a
                href="https://daten.berlin.de/datensaetze/baustellen-sperrungen-und-sonstige-storungen-von-besonderem-verkehrlichem-interesse"
                target="_blank"
                rel="noreferrer"
              >
                Digitale Plattform Stadtverkehr Berlin
              </a>{" "}
              (Datenlizenz Deutschland – Namensnennung 2.0)
            </p>
          </div>
        </div>
      )}
    </>
  );
}
