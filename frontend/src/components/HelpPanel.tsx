import { useState } from "react";

const HELP_SEEN_STORAGE_KEY = "wattloop_help_seen";

// Nur zur Steuerung des initialen Zustands (Panel beim allerersten Besuch offen) - kein
// Muss, daher toleriert dies auch einen blockierten/fehlenden localStorage stillschweigend.
function hasSeenHelpBefore(): boolean {
  try {
    return localStorage.getItem(HELP_SEEN_STORAGE_KEY) === "1";
  } catch {
    return false;
  }
}

function markHelpAsSeen() {
  try {
    localStorage.setItem(HELP_SEEN_STORAGE_KEY, "1");
  } catch {
    // localStorage kann in privaten/eingeschraenkten Modi fehlschlagen - unkritisch, das Panel
    // wird beim naechsten Besuch dann einfach erneut standardmaessig geoeffnet.
  }
}

export function HelpPanel() {
  const [open, setOpen] = useState(() => !hasSeenHelpBefore());

  function close() {
    setOpen(false);
    markHelpAsSeen();
  }

  return (
    <>
      <button
        type="button"
        className="map-icon-button help-button"
        onClick={() => setOpen(true)}
        aria-label="Hilfe anzeigen"
        title="Hilfe"
      >
        ?
      </button>

      {open && (
        <div className="modal-overlay" onClick={close}>
          <div className="modal-panel help-panel" onClick={(e) => e.stopPropagation()}>
            <div className="modal-panel-header">
              <h2>Kurzanleitung</h2>
              <button type="button" className="modal-close" onClick={close} aria-label="Schließen">
                ×
              </button>
            </div>

            <dl>
              <dt>Kartenklick</dt>
              <dd>
                Ein Klick auf die Karte öffnet ein Popup mit drei Optionen: Startpunkt setzen,
                den Punkt als Pflicht-Wegpunkt in die Route einschließen, oder einen Abschnitt
                um diesen Punkt herum sperren.
              </dd>

              <dt>FIT-Datei hochladen vs. Workout zusammenstellen</dt>
              <dd>
                Entweder eine bestehende FIT-Workout-Datei hochladen, oder das Workout direkt im
                Editor aus Blöcken (z. B. Warmup, Intervalle, Erholung) zusammenstellen - beides
                führt zum selben Ergebnis.
              </dd>

              <dt>Nutzerprofil</dt>
              <dd>
                FTP, Gewicht und Sprint-Ø-Watt fließen in die Zeitschätzung ein und werden nach
                jeder Routenberechnung automatisch im Konto gespeichert.
              </dd>

              <dt>Limit-Felder</dt>
              <dd>
                Die Felder zu unbefestigtem Untergrund und Ampeln/Kreuzungen sind optional -
                leer gelassen gilt kein Limit. "Anzahl Streckenvarianten" wirkt nur, wenn eines
                der beiden Limits gesetzt ist: WattLoop probiert dann mehrere Routenvarianten
                durch, um das Limit einzuhalten.
              </dd>

              <dt>Geplanter Fahrzeitpunkt</dt>
              <dd>
                Optional - wird ein Zeitpunkt angegeben, fließt die dafür vorhergesagte
                Windrichtung/-stärke in die Zeitschätzung ein.
              </dd>

              <dt>Ampeln/Stoppschilder-Layer</dt>
              <dd>
                Blendet Ampeln und Stoppschilder als zusätzlichen Layer auf der Karte ein, um
                die Streckenwahl besser einschätzen zu können.
              </dd>

              <dt>Baustellen (🚧-Button)</dt>
              <dd>
                Zeigt die automatisch erkannten Baustellen-Sperrungen (Berlin) in einer eigenen
                Liste an, inklusive der Möglichkeit, einzelne Einträge für die aktuelle Route zu
                ignorieren. Ein eigener Kartenlayer (ebenfalls in den Einstellungen ein-/
                ausblendbar) zeigt sie zusätzlich direkt auf der Karte.
              </dd>

              <dt>GPX-Download</dt>
              <dd>Lädt die berechnete Route nach der Berechnung als GPX-Datei herunter.</dd>
            </dl>
          </div>
        </div>
      )}
    </>
  );
}
