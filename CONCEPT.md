# Fahrrad-Trainingsrouten-Planer — Konzept

## 1. Idee

Eine Applikation, die zu einem strukturierten Radtraining (Startpunkt + Trainingsplan mit
Belastungszonen) automatisch eine **möglichst passende Route** berechnet. Kernkriterien:

- Streckenlänge passend zur Gesamtdauer des Trainings
- Intensive Belastungsabschnitte (z. B. Intervalle) sollen auf Straßenabschnitten liegen, die sich
  **ohne Unterbrechung** (keine Ampeln/Stopps, möglichst keine Vorfahrt-Verluste) durchfahren lassen
- Straßenbelag als weiteres Kriterium

Primäre Betriebsart: **Rundkurs** (Start = Ziel). A-nach-B ist ein mögliches späteres Feature,
aber nicht Teil des aktuellen Konzepts.

## 2. Stack

| Komponente | Wahl | Begründung |
|---|---|---|
| Routing-Engine | **GraphHopper** (self-hosted, Java) | bringt `round_trip`-Algorithmus (Schleife ab Startpunkt) und Custom-Model-Gewichtung (Belag, Straßentyp) sowie Elevation-Support bereits mit |
| Backend | **C# / ASP.NET Core** | Orchestrierung: FIT-Parsing, Leistungsmodell, Korridor-Scoring, Routen-Konstruktion, Ansteuerung von GraphHopper via REST |
| Frontend | **React + TypeScript + MapLibre GL** | ausgereiftes Kartenökosystem für Routen-Overlays/Interaktion; C# (Blazor) wurde erwogen, aber wegen dünnerem Geo-Ökosystem verworfen |
| Geodaten | **OpenStreetMap** (Geofabrik-Extrakt für GraphHopper-Import) | Tags für Straßentyp, Ampeln, Vorfahrt, Belag verfügbar |

Zielbild: zunächst **Prototyp**, aber mit Blick darauf, später für mehrere Nutzer / öffentlich
nutzbar zu werden (kein Design, das das verbaut).

## 3. Domänenmodell

### 3.1 Nutzerprofil

- FTP (Watt)
- Gewicht (Rad + Fahrer)
- Sprint-Ø-Leistung (Watt, siehe 3.2 — unabhängig von FTP)
- Optional (fortgeschritten, überschreibbar): CdA, Crr, Antriebswirkungsgrad

Defaults, falls nicht angegeben: CdA ≈ 0.3–0.4 m², Crr ≈ 0.005 (Asphalt), Wirkungsgrad ≈ 97 %.

### 3.2 Trainingszonen

Klassische Rad-Zonen: **GA1, GA2, EB, SB, VO2max, Sprint**.

Zonen (außer Sprint) werden als **%FTP-Band** ausgedrückt. Sprint wird **nicht** von der FTP
abgeleitet (neuromuskulärer Kurzzeit-Ausbruch, keine FTP-Korrelation), sondern über einen vom
Nutzer angegebenen **festen Ø-Watt-Wert**.

Wichtige Vereinfachung: Auch für Sprint wird **keine Beschleunigungsphase** modelliert — die
gesamte Zonendauer wird als konstante Zielleistung (steady state) betrachtet. Das ist konsistent
mit der generellen Vereinfachung, dass für alle Zonen Beschleunigung/Ausrollen ignoriert werden.

Die **Unterbrechungstoleranz** (siehe 3.4) ist nicht an den Zonennamen gekoppelt, sondern an das
**%FTP-Band** — dadurch funktioniert das Scoring unabhängig davon, ob eine Zone manuell benannt
oder aus einer importierten FIT-Datei mit rohem Leistungsbereich übernommen wurde.

### 3.3 Leistungs-/Geschwindigkeitsmodell

Physikalisches Steady-State-Modell:

```
P = P_luft + P_roll + P_steigung
P_luft      ≈ 0.5 · ρ · CdA · v³
P_roll      ≈ Crr · m · g · v · cos(α)
P_steigung  ≈ m · g · v · sin(α)
```

- Wind wird für den Prototyp **ignoriert** (spätere Erweiterung möglich)
- Beschleunigung/Ausrollen wird **ignoriert** (steady state für alle Zonen inkl. Sprint)
- v wird aus P numerisch gelöst (z. B. Newton-Verfahren)

Zwei Berechnungsebenen:

1. **Grobschätzung (flach angenommen)** — liefert eine erste Zieldistanz für die
   GraphHopper-Rundtour-Anfrage, bevor überhaupt ein Höhenprofil bekannt ist
2. **Höhenprofil-adjustiert** — sobald ein Kandidaten-Segment/eine Kandidatenroute mit
   Elevation-Daten vorliegt, wird die tatsächliche Zeit pro Abschnitt neu berechnet

Die Gesamtroutenlänge für die Zieldauer ist dadurch ein **iterativer Prozess**: grob schätzen →
Route/Höhenprofil holen → tatsächliche Zeit anhand des echten Profils berechnen → bei zu großer
Abweichung angefragte Distanz anpassen (z. B. per Bisektion) und wiederholen.

### 3.4 Unterbrechungs-Score

Jedes Straßensegment bekommt einen additiven Score aus den Kreuzungen/Hindernissen entlang der
Strecke:

| Merkmal (OSM-Tag) | Einstufung | Score-Beitrag |
|---|---|---|
| `highway=traffic_signals`, `highway=stop` | Vollstopp, unvorhersehbare Wartezeit | **harter Ausschluss** (bricht jeden Korridor) |
| `junction=roundabout` | Tempo raus, meist kein Vollstopp | mittel (z. B. +2) |
| `highway=give_way`, ungeregelte Kreuzung ohne klare Vorfahrt | potenzielles Abbremsen | mittel-niedrig (z. B. +1) |
| Kreuzende Straße niedrigerer Klasse ohne Ampel/Schild (faktische Vorfahrt) | quasi risikofrei | sehr gering (z. B. +0.2) |
| Kreuzende Straße gleicher/höherer Klasse ohne Ampel/Schild ("Rechts vor links") | gesetzliche Pflicht zum Schauen/Bremsen (in D), genauso störend wie Give-way | wie Give-way (z. B. +1.0) |

Score eines Segments = 1 (Basis) + Summe der Beiträge aller (nicht-harten) Kreuzungen entlang des
Wegs. Jedes %FTP-Band hat einen konfigurierbaren **maximal tolerierten Score** (z. B. VO2max nahe
1 = keine Unterbrechung, EB deutlich toleranter = z. B. ein Kreisverkehr okay). Gewichte und
Schwellwerte sind Konfigurationswerte, keine im Code verdrahteten Sonderfälle pro Zone.

## 4. Kernalgorithmus: Korridore finden & verketten

### 4.1 Korridor-Vorberechnung (einmalig pro Region, cachebar)

- OSM-Straßennetz als Graph: Knoten an Kreuzungen/Sonderpunkten (inkl. Ampeln/Stopps/Give-way),
  Kanten = Straßenstücke mit Länge + Belag
- Score-Beitrag pro Knoten wird bestimmt (siehe 3.4)
- **Maximale Korridore** werden gebildet: zusammenhängende Kantenfolgen, die an harten
  Ausschluss-Knoten enden, aber durch alle weichen Kreuzungen hindurchlaufen
- Pro Korridor wird ein **Score-Distanz-Profil** (Präfixsumme der Scores entlang der Distanz)
  gespeichert
- Da Score-Beiträge nie negativ sind, lässt sich mit einem **Sliding-Window/Zwei-Zeiger-Verfahren**
  in O(n) pro Korridor beantworten: „Gibt es eine zusammenhängende Teilstrecke von mindestens
  Länge D mit kumuliertem Score ≤ S?" (inkl. exaktem Start-/Endpunkt falls ja)
- Diese Abfrage ist zur Laufzeit einer Trainingsanfrage sehr billig, da die teure Vorarbeit
  einmalig pro Region passiert ist

### 4.2 Routen-Konstruktion (pro Trainingsanfrage)

Zweistufiges Vorgehen, um kein eigenes globales Optimierungsproblem lösen zu müssen:

1. **Grobe Loop-Form:** GraphHopper `round_trip` liefert eine plausible Rundtour der
   ungefähren Ziellänge ab dem Startpunkt (rein geografisches Gerüst, ignoriert Intervalle)
2. **Korridor-Splicing:** Für jeden Intervall-Schritt aus dem Trainingsplan (in der bekannten
   zeitlichen/Distanz-Reihenfolge) wird an der entsprechenden ungefähren Position der groben Loop
   in der Nähe nach einem passenden vorberechneten Korridor gesucht (Distanz- und
   Score-Anforderung aus dem höhenprofil-korrigierten Leistungsmodell). Bei Treffer wird der
   Loop-Abschnitt durch Korridor-Eingang → Korridor → Korridor-Ausgang ersetzt
3. **Finale Route:** Die resultierende Wegpunktkette wird als **Multi-Waypoint-Anfrage** an
   GraphHopper gestellt — GraphHopper übernimmt das Feintuning des Pfades zwischen den Punkten,
   wir kontrollieren nur welche Punkte in welcher Reihenfolge

**Segment-Wiederverwendung:** Bei wiederholten Intervallen gleicher Zone (z. B. 3×5min EB) ist es
zulässig und im echten Training üblich, denselben Korridor mehrfach zu befahren. Dies ist eine
**Nutzereinstellung** (Präferenz „Wiederholungen am selben Ort" vs. „möglichst viel neue Strecke"),
kein Algorithmus-Zwang. Bei „Vielfalt" wird aktiv nach mehreren unabhängigen Korridoren gesucht,
mit Reuse als Fallback falls nicht genug gefunden werden.

### 4.3 Fallback-Strategie

Eskalationskette, falls kein Korridor Score-Schwelle **und** benötigte Länge erfüllt:

1. **Strikt versuchen** (konfigurierte Schwelle + normaler Suchradius)
2. **Automatisch lockern:** Suchradius schrittweise vergrößern und/oder Score-Schwelle leicht
   anheben, mehrfach retry bis zu sinnvollen Obergrenzen
3. **Bestmöglichen Kandidaten nehmen + transparent kennzeichnen:** falls auch das nichts liefert —
   Route wird trotzdem erzeugt, der Abschnitt wird in der UI klar markiert (z. B. „keine perfekte
   Lösung gefunden, dieser Abschnitt hat ggf. eine Kreuzung ohne Vorfahrt")

Kein harter Fehlschlag außer in Extremfällen; der Nutzer sieht immer ehrlich, wenn ein Kompromiss
gemacht wurde.

## 4.4 Anfahrt/Abfahrt zur eigentlichen Trainingsstrecke

Bei einem Startpunkt in einer dicht bebauten Stadt (z. B. Berlin) existieren in unmittelbarer
Nähe vermutlich keine ampelfreien Vorfahrtsstraßen — geeignete Korridore liegen ggf. erst einige
Fahrminuten außerhalb. Dafür gibt es einen neuen Parameter:

- **Max. Anfahrtszeit** (Default z. B. 30 min, konfigurierbar im Nutzerprofil/pro Anfrage, kein
  hartkodierter Wert) — begrenzt, wie weit der Korridor-Suchradius vom Startpunkt entfernt sein
  darf. Gilt als **Gesamtbudget für Hin- und Rückweg zusammen**, nicht pro Richtung einzeln.

**Budget-Absorption:** Ruhige Trainingsblöcke (Warmup/GA1/Erholung) haben ohnehin eine sehr hohe
Score-Toleranz und können auf jeder Straße gefahren werden, auch mitten durch die Stadt. Ihre
Distanz wird deshalb automatisch als "Budget" für die Anfahrt/Abfahrt verwendet, statt die Anfahrt
immer als reine Zusatzzeit zu behandeln — die Fahrt aus der Stadt heraus ist dann einfach Teil des
ohnehin geplanten ruhigen Streckenanteils. Nur wenn dieses Budget nicht ausreicht (z. B. Plan hat
kaum ruhige Blöcke, oder die Stadt ist zu groß), kommt zusätzliche Zeit oben drauf — begrenzt durch
die max. Anfahrtszeit, und transparent gekennzeichnet (analog zum Korridor-Fallback aus 4.3: "Route
ist X min länger als der reine Trainingsplan, weil die nächste geeignete Strecke soweit entfernt
liegt").

## 5. Trainings-Input (MVP)

- **Nur FIT-Import** für den Start (kein manueller Block-Builder in v1). Ein FIT-Workout-File
  enthält bereits die vollständige geordnete Blockliste (`workout_step`-Nachrichten mit Dauer,
  Zielleistungsbereich, `repeat_steps`-Strukturen für Wiederholungen) — deckt sich mit dem, was
  wir ohnehin brauchen, ohne dass wir dafür selbst eine Eingabe-UI bauen müssen
- FTP-Wert kommt **aus dem manuell gepflegten Nutzerprofil**, nicht aus der FIT-Datei
- Benötigt: FIT-Parser für C# (z. B. Garmin FIT SDK mit C#-Bindings oder Community-Library)

## 6. MVP-Scope / Roadmap

Orientierung an technischem Risiko statt Feature-Vollständigkeit: Der Korridor-/
Verkettungsalgorithmus ist der unsichere Teil und wird deshalb zuerst validiert, bevor Zeit in
UI/Infrastruktur auf einer ungetesteten Kernannahme investiert wird.

### Phase 0 — Machbarkeits-Spike (kein Produkt, nur Validierung)

- GraphHopper lokal mit einem kleinen OSM-Extrakt (Heimregion) aufsetzen, `round_trip` +
  Multi-Waypoint-Routing manuell über die API testen
- Korridor-Erkennung als Skript/Notebook gegen echte OSM-Daten prototypen: Existieren in einer
  realen Region überhaupt genug lange, störungsarme Korridore? Dies ist die Kernannahme des
  gesamten Projekts und sollte so früh wie möglich bestätigt oder widerlegt werden
- Ergebnis: Go/No-Go-Entscheidung für den Ansatz, plus erste Kalibrierung der Score-Gewichte an
  echten Daten

### Phase 1 — Kern-Algorithmus als Service/Library (noch ohne UI)

- OSM-Korridor-Vorberechnungs-Pipeline (Batch-Job)
- Leistungsmodell (Watt→Speed, flach + höhenprofil-adjustiert) mit Tests
- FIT-Parser-Integration
- Routen-Konstruktionsalgorithmus (Korridor-Suche + Verkettung + Fallback-Eskalation, siehe 4.3)
- Minimaler HTTP-Endpoint: (Startpunkt, FIT-Datei, Profil) → Route als GeoJSON/GPX, getestet via
  curl/Postman + Visualisierung in einem einfachen Kartentool (noch kein eigenes Frontend)

### Phase 2 — Nutzbares Minimalprodukt

- React-Frontend: Startpunkt auf Karte wählen, FIT-Datei hochladen, generierte Route ansehen
- **GPX-Export** für Radcomputer/Garmin — die Route muss auf einer echten Fahrt navigierbar sein,
  reine Kartenanzeige im Browser reicht nicht als Zieldefinition für den MVP
- Einfaches Nutzerprofil (FTP, Gewicht) — **kein Auth/Multi-User** in dieser Phase, Single-User-
  Betrieb reicht aus

### Phase 3 — Realer Einsatz & Kalibrierung

- Mit echten Trainingsfahrten testen, Score-Gewichte/Schwellwerte anhand der Erfahrung
  nachjustieren
- Segment-Wiederverwendung-Einstellung in der UI ergänzen (Präferenz „gleicher Ort" vs.
  „Streckenvielfalt", siehe 4.2)

### Phase 4 — Später (nicht Teil des MVP)

- Mehrbenutzerfähigkeit, Auth, Hosting/Deployment für andere Nutzer
- Manuelle Block-Builder-UI als Alternative zum FIT-Import
- A-nach-B-Routing (statt nur Rundkurs)
- Windmodellierung (aktuell bewusst ignoriert)

## 6.1 Phase 0 — Ergebnis (durchgeführt)

**Startpunkt:** Sportforum Berlin (52.5426187, 13.4763778), Radius 60 km (Berlin + Brandenburg,
via Geofabrik-Extrakte, mit `osmium` auf die Bounding Box zugeschnitten).

**Korridor-Machbarkeit:** Validiert. Ein Python-Spike-Skript (`phase0-spike/scripts/corridor_check.py`,
nutzt pyosmium statt des defekten `pyrosm`/`pyrobuf`) hat den Straßengraphen aufgebaut, Korridore
zwischen harten Ausschluss-Knoten extrahiert und mit der Sliding-Window-Technik aus Abschnitt 4.1
geprüft. Ergebnis:

| Zonen-Schwelle | ≥1000 m | ≥2000 m | ≥3000 m | längster Treffer |
|---|---|---|---|---|
| VO2max/Sprint (~1) | 917 | 421 | 269 | 54.851 m |
| SB (~1.5) | 999 | 450 | 289 | 54.851 m |
| EB (~3) | 1198 | 529 | 335 | 54.851 m |

(Werte nach Korrektur der Rechts-vor-links-Erkennung, siehe Fehlerliste unten — ursprünglich
minimal höher, da unbeschilderte Kreuzungen zunächst zu optimistisch bewertet wurden.)

Selbst bei der strengsten Schwelle (praktisch keine Unterbrechung) existieren im 60-km-Umkreis
Hunderte geeignete Korridore — die Kernannahme des Projekts ist für diese Region bestätigt.

**GraphHopper-Fähigkeiten:** Validiert. `round_trip`-Algorithmus liefert eine Schleife nahe der
Zieldistanz (angefragt 15 km → 13,2 km geliefert); Multi-Waypoint-Routing führt korrekt durch eine
vorgegebene Punktreihenfolge (4 Wegpunkte, 33,5 km Gesamtroute). Wichtig: `round_trip` ist mit
Contraction-Hierarchies-Vorberechnung (CH) nicht kompatibel — für Phase 1 muss das Profil ohne CH
(oder mit separatem Nicht-CH-Profil) betrieben werden.

**Drei Implementierungsfehler/-lücken während des Spikes gefunden und behoben** (relevant für
Phase 1):
1. Korridor-Extraktion brach ursprünglich an jeder echten Kreuzung ab statt nur an harten
   Ausschluss-Knoten (Ampel/Stopp) — widersprach dem Konzept aus Abschnitt 4.1. Behoben durch
   Fortsetzen durch weiche Kreuzungen mit einer "Geradeaus"-Heuristik (kleinste Winkeländerung)
   bei Abzweigungen.
2. Die Sliding-Window-Funktion hatte einen Off-by-one-Fehler: bei groben Kantenlängen (lange
   Landstraßen mit wenigen Stützpunkten) konnte sie ein gültiges Fenster überspringen. Behoben
   durch Lookahead vor dem Verschieben des linken Zeigers.
3. Unbeschilderte Kreuzungen wurden anfangs alle gleich behandelt (fester Malus), ohne zwischen
   faktischer Vorfahrt (unsere Straße höherrangig) und Rechts-vor-links (gleich-/höherrangige
   Kreuzung ohne Beschilderung — in Deutschland gesetzliche Pflicht zum Schauen/Bremsen) zu
   unterscheiden. Behoben durch Vergleich der Straßenklassen an jeder unbeschilderten Kreuzung
   (siehe Score-Tabelle in Abschnitt 3.4). Nach der Korrektur sinken die Korridor-Zahlen nur
   moderat (z. B. VO2max/Sprint ≥1000 m: 989 → 917), die Go-Entscheidung bleibt bestehen.

**Go/No-Go:** **Go.** Ansatz trägt für Phase 1.

**Fundstellen:** `phase0-spike/` (nicht Teil der C#-Produktionspipeline, Wegwerf-Code).

## 7. Offene Punkte

- Kalibrierung der genauen Score-Gewichte und Zonen-Schwellwerte (aktuell Platzhalter-Werte, die
  in Phase 0/3 anhand echter Daten getunt werden müssen)
- Konkrete Wahl der FIT-Parser-Library für C#
