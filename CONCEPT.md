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
- A-nach-B-Routing (statt nur Rundkurs)
- Nutzer setzt manuell eigene Wegpunkte, um die berechnete Route gezielt anzupassen
  (z. B. einen bestimmten Abschnitt umgehen oder erzwingen)
- Windmodellierung (aktuell bewusst ignoriert)
- Integration von Baustellen (aktuelle Straßensperrungen/-einschränkungen in die Korridor-/
  Routenbewertung einbeziehen, z. B. über OSM `construction`-Tags oder externe Baustellen-Feeds)
- Nutzer können Segmente selbst bewerten/sperren (z. B. "diese Straße meiden" dauerhaft im Profil
  hinterlegen, unabhängig vom automatischen Score aus 3.4)
- Straßenauslastung/Verkehr zur geplanten Trainingsuhrzeit in die Streckenbewertung einbeziehen
  (Nutzer gibt an, wann trainiert werden soll; stark befahrene Straßen zu der Uhrzeit werden
  gemieden). OSM selbst führt keine Verkehrsvolumen-/Stauzeitdaten — bräuchte eine externe
  Verkehrsdaten-API (z. B. TomTom Traffic, HERE Traffic; meist kostenpflichtig/kontingentiert)
  oder ersatzweise eine grobe Heuristik über Straßenklasse + Tageszeit ohne Echtzeitdaten
- TCX-Export mit typisierten `<CoursePoint>`-Elementen (z. B. "Segment Start"/"Segment End" statt
  generischer Icons) als kleine Verbesserung zu den bereits vorhandenen benannten GPX-Wegpunkten
  aus Abschnitt 6.5 — reine Icon-/Kategorisierungs-Verbesserung, kein neues Verhalten
- **Geprüft und verworfen:** FIT-Workout-Export für Geräte-native Restdistanz-Anzeige pro
  Intervall. Grund: Ein FIT-Workout-Schritt läuft rein zeit-/distanzbasiert relativ zum Start des
  Schritts (Timer/Distanzzähler ab Schrittbeginn) — er hat keinerlei GPS-Bezug zur gleichzeitig
  geladenen Route/den gewählten Korridoren. Er würde nur hoffen, dass der Fahrer nach X Minuten
  zufällig am richtigen Korridor ist, statt es sicherzustellen. Echte positionsgebundene
  Live-Restdistanz-Anzeige gibt es auf Garmin/Wahoo nur über deren eigenes "Segment"-Feature
  (Garmin Connect/Strava) — das wäre eine echte Plattform-API-Integration (Account-Anbindung,
  Segment dort anlegen/synchronisieren), kein Datei-Export-Thema, und ein deutlich größeres
  eigenständiges Vorhaben als alles bisher in Phase 2. Die bereits gebauten benannten
  GPX-Wegpunkte (Abschnitt 6.5) bleiben damit die beste aktuell umsetzbare Lösung, da sie
  tatsächlich positionsgebunden (GPS-Näherungsalarm) sind, nur ohne mitlaufende Distanzanzeige.

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

## 6.2 Phase 1 — Status (durchgeführt)

C#-Lösung unter `src/` (TrainingRoutePlanner.slnx, .NET 10): Domain, PowerModel, OsmCorridors,
FitParsing, RouteEngine, Api, Tests. **36/36 Tests grün, 0 Build-Warnungen.**

- **Domain**: Nutzerprofil, Trainingszonen/-schritte, `ZoneResolver` (Zone/％FTP/absolute Watt →
  Zielleistung + Score-Schwelle), `Corridor`/`ICorridorIndex`, `RouteRequest`/`RouteResult`.
- **PowerModel**: Watt→Speed-Löser (Newton-Verfahren) nach Abschnitt 3.3, 8 Tests.
- **OsmCorridors**: 1:1-Port des validierten Python-Spikes nach C#/OsmSharp. Auf dem echten
  60-km-Extrakt liefert der Port **exakt dieselben Korridor-Zahlen** wie der Python-Spike
  (Abschnitt 6.1) — starke Bestätigung, dass der Port korrekt ist. 15 Tests gegen handgebaute
  synthetische Graphen (kein Test gegen die echte PBF-Datei, zu langsam für eine Testsuite).
- **FitParsing**: FIT-Workout-Parser auf Basis des offiziellen `Garmin.FIT.Sdk`. Tests nutzen die
  SDK-eigene Encode-API, um Workout-Dateien selbst zu bauen (kein externes Sample-File nötig). 6
  Tests, inkl. Wiederholungs-Entrollung, %FTP-Zielwert-Dekodierung, Fallbacks für Nicht-Leistungs-
  bzw. Open-Dauer-Schritte. **Bekannte Lücke:** ein SDK-Bug (Garmin.FIT.Sdk 21.214.0) korrumpiert
  `wkt_step_name` für alle Schritte einer Datei, sobald ein Schritt (typischerweise der
  Wiederholungs-Marker) keinen Namen setzt — betrifft nur die Anzeige-Labels, nicht Dauer/Leistung/
  Score. Nicht selbst behebbar (liegt in der Drittanbieter-SDK), als Kommentar an der Lesestelle
  dokumentiert.
- **RouteEngine**: `GraphHopperClient` (round_trip + Multi-Waypoint) und
  `RouteConstructionService` (Korridor-Splicing + Eskalationskette aus 4.2/4.3), 8 Tests mit
  Fakes für GraphHopper/CorridorIndex.
- **Api**: Minimaler Endpoint `POST /route` (multipart: FIT-Datei + Profil-Felder) verdrahtet alle
  Module zusammen.

**Nacharbeit nach Phase 1 (durchgeführt):**
- **Höhenprofil-iterative Distanzverfeinerung** (3.3): `RouteConstructionService` fragt jetzt
  `round_trip` an, misst die tatsächliche Steigung entlang der Antwort an der ungefähren
  Position jedes Trainingsschritts (400 m Fenster) und berechnet die Zieldistanz mit dem
  höhenprofil-adjustierten Leistungsmodell neu. Bei >5 % Abweichung wird `round_trip` mit der
  verfeinerten Distanz erneut angefragt (max. 3 Iterationen). Dabei einen echten Bug gefunden:
  GraphHopper liefert Elevation nur mit explizitem Query-Parameter `elevation=true`, obwohl der
  Server-seitige Elevation-Support aktiviert war — ohne diesen Parameter bleiben alle
  Höhenwerte `null`, und die Verfeinerung hätte still nie gegriffen. Behoben in
  `GraphHopperClient`.
- **Aktive Anfahrt-Budget-Nutzung** (4.4): `RouteRequest.MaxApproachMinutes` wurde vorher gar
  nicht verwendet (totes Feld). Jetzt wird daraus (bei GA1-Tempo, Budget für Hin+Rück zusammen)
  ein maximaler Korridor-Suchradius abgeleitet, den die Fallback-Eskalation aus 4.3 nicht mehr
  überschreitet — statt einer festen Anzahl Lockerungsversuche ist die Eskalation jetzt an das
  tatsächliche Zeitbudget des Nutzers gekoppelt.
- Beides end-to-end gegen echten GraphHopper + echten Datenextrakt verifiziert (Elevation-Werte
  korrekt im Antwort-JSON, 38/38 Tests grün inkl. 2 neuer Tests für die beiden Verhalten).

**Verbleibende bewusste Vereinfachung:**
- Korridor-Suche ist ein linearer Scan mit Bounding-Box-Vorfilter (kein Spatial Index) — für die
  MVP-Korridoranzahlen ausreichend, bei viel größeren Regionen ggf. zu optimieren.

**End-to-End-Rauchtest (manuell, mit echtem GraphHopper + echtem 60-km-Extrakt):** Ein
FIT-Workout (20 min Grundlage + 3×[3 min Work, 2 min Recovery]) wurde über `POST /route`
eingereicht und lieferte eine vollständige 27,5-km-Route mit 572 Geometriepunkten — inklusive
genau der erwarteten Transparenz-Warnungen (Korridor-Fallback ausgelöst, Anfahrt-Budget
überschritten). Bestätigt, dass die komplette Kette (FIT-Import → Leistungsmodell →
Korridor-Suche → GraphHopper → Fallback-Eskalation) end-to-end funktioniert.

## 6.3 Phase 2 — Nacharbeit: Kehrtwenden-Vermeidung (durchgeführt)

Bei manuellen Tests fiel auf: Wird derselbe Korridor für mehrere Wiederholungen desselben
Trainingsschritts wiederverwendet (Standardeinstellung "Gleicher Ort"), muss die Route vom
Korridorende zurück zum -anfang - ohne alternative Straße in der Nähe geht das oft nur per
Kehrtwende. Neuer Parameter **`RouteRequest.AllowUTurns`** (Default `true`):

- Bei `false` wird die exakte Korridor-Wiederverwendung deaktiviert (Verhalten faellt effektiv
  auf "Streckenvielfalt" zurück), damit jede Wiederholung eine eigene Verbindungsstrecke ohne
  erzwungenen Rückweg bekommt.
- Zusätzlich prüft `PolylineMath.DetectSharpReversals` die finale Routen-Geometrie auf abrupte
  Richtungswechsel (Peilungsvergleich kurz vor/nach jedem Punkt, Schwelle ~150°) und meldet
  verbleibende Kehrtwenden transparent als Warnung — kann in dünnen Straßennetzen (z. B. echte
  Sackgassen) nicht immer vollständig vermieden werden, folgt aber derselben "kein harter
  Fehlschlag, aber transparent gekennzeichnet"-Philosophie wie die Fallback-Eskalation (4.3).

Frontend: Checkbox "Kehrtwenden erlauben" im Formular. End-to-End getestet (echtes Chrome via
Claude-in-Chrome-Erweiterung) — bei deaktivierter Checkbox erscheinen tatsächlich unterschiedliche
Korridor-Segmente statt Wiederverwendung, plus ehrliche Kehrtwenden-Warnungen dort, wo das
Straßennetz keine Alternative bot.

## 6.4 Phase 2 — Segment-Einfärbung nach Trainingsschritt (durchgeführt)

`RouteResult` trägt jetzt zusätzlich zur Gesamt-Geometrie eine Liste **`Segments`**
(`RouteSegment { Label, Geometry }`) — ein Eintrag pro gefundenem Effort-Korridor, mit dem Label
des zugehörigen Trainingsschritts (z. B. "Work"). Frontend zeichnet die Gesamtroute als dünne
blaue Linie und legt die Segmente in individuellen Farben (feste Palette, pro Label konsistent)
darüber, mit Legende in der Seitenleiste — Nutzer erkennen so die Intervalle aus dem FIT-File auf
der Karte wieder.

## 6.5 Phase 2 — Segment-Markierung im GPX-Export (durchgeführt)

`GpxWriter` schreibt jetzt zusätzlich zu den Track-Punkten benannte Wegpunkte (`<wpt>`) am Start
und Ende jedes `RouteSegment` (z. B. "Start: Work (1)" / "Ende: Work (1)", durchnummeriert bei
Wiederholung), platziert vor `<trk>` gemäß GPX-1.1-Element-Reihenfolge. Garmin- und
Wahoo-Geräte zeigen beim Abfahren einer geladenen Kurs-Datei ein Pop-up, sobald ein benannter
Wegpunkt in der Nähe erreicht wird — das ist tatsächlich positionsgebunden (GPS-Näherungsalarm),
nur ohne mitlaufende Restdistanz-Anzeige (siehe Abwägung zum FIT-Workout-Export in Abschnitt 6
Phase 4). Noch nicht am echten Gerät getestet, nur end-to-end gegen die API verifiziert.

## 6.6 Phase 2 — Manueller Block-Editor als FIT-Alternative (durchgeführt)

Damit Tester ohne vorhandene FIT-Datei starten können: Frontend hat jetzt einen Tab
"Workout zusammenstellen" neben dem FIT-Upload — Nutzer bauen den Plan direkt aus Zonen-Blöcken
(GA1/GA2/EB/SB/VO2max) und Wiederholungsgruppen (`+ Wiederholung`, mit beliebig vielen inneren
Schritten). Neuer Endpoint **`POST /workout/build`** (`FitWorkoutEncoder` in
TrainingRoutePlanner.FitParsing) erzeugt daraus eine **echte FIT-Workout-Datei** — bewusst über
den existierenden `FitWorkoutParser` statt eines separaten Codepfads, damit Editor-Plan und
FIT-Upload exakt denselben Rest der Pipeline durchlaufen. Zielleistung wird als %FTP-Bereich
kodiert (Bandgrenzen aus `ZoneBands`, nicht absolute Watt) — macht die generierte Datei
nutzerprofil-unabhängig wiederverwendbar. **Sprint wird nicht unterstützt** (nicht %FTP-basiert,
siehe ZoneBands), Encoder wirft `NotSupportedException` bei einem Sprint-Block.

3 Rundreise-Tests (Encode → Parse, inkl. Wiederholungsgruppen und Sprint-Ablehnung) plus
End-to-End-Verifikation: Editor-Plan → generierte FIT-Bytes → vollständige Routenberechnung mit
korrekt zugeordnetem Segment, live im echten Chrome bestätigt.

## 6.7 Phase 2 — Deployment auf Render.com (durchgeführt)

Backend (Docker), GraphHopper (Docker) und Frontend (Static Site) als Render-Blueprint
(`render.yaml`) deployt, siehe [DEPLOY.md](DEPLOY.md). Zwei reale Produktions-Bugs erst nach dem
Live-Deploy gefunden (in keinem lokalen Test/Docker-Smoke-Test vorher aufgefallen):

- **API-Container crashte beim Start** mit `IOException: configured user limit (128) on
  inotify instances reached`. Renders Container-Sandbox erlaubt nur sehr wenige inotify-Watches;
  ASP.NET Core registriert standardmäßig einen `FileSystemWatcher` pro `appsettings*.json` für
  Runtime-Reload. Fix: `DOTNET_hostBuilder__reloadConfigOnChange=false` als Env-Var (wird von
  `CreateBuilder` schon beim Bootstrap gelesen) — Config ändert sich bei uns ohnehin nur per
  Redeploy, Runtime-Reload wird nie gebraucht.
- **Frontend berechnete Routen korrekt, zeichnete aber nie eine Linie** — GeoJSON-Sources
  blieben permanent ungeladen, kein Fehler irgendwo. Ursache (gefunden durch direktes Inspizieren
  von MapLibres internem Tile-Manager/Actor-State im echten Browser): maplibre-gl bringt seinen
  eigenen Web Worker mit; sobald Rolldown (Vite 8s Bundler) diesen Code für den Produktions-Build
  selbst transformiert/neu emittiert — egal ob als eigener Chunk oder inline, minifiziert oder
  nicht, alle Kombinationen getestet — hört das Zusammenspiel zwischen Hauptthread und Worker
  stillschweigend auf zu funktionieren. Der Dev-Server war davon nie betroffen
  (`optimizeDeps.exclude` liefert node_modules unangetastet aus), das gilt aber nur für den
  Dev-Server, nicht für `vite build`. Fix: `postinstall`-Skript kopiert maplibre-gls eigene,
  unveränderte Dist-Dateien nach `public/vendor/maplibre-gl`; `build.rollupOptions.external`
  hält den `"maplibre-gl"`-Import komplett aus Rolldowns Bundling raus, eine Import-Map in
  `index.html` löst ihn zur Laufzeit im Browser zur unveränderten Vendor-Kopie auf.

Beide Bugs zeigten sich ausschließlich im echten Produktions-Build unter Render — Lehre für
künftige Deployments: den tatsächlichen `vite build`-Output lokal (z. B. via `vite preview`)
in einem echten Browser testen, nicht nur `vite dev` und den API-Container isoliert.

## 6.8 Phase 2 — Straßenbelag farblich markieren (durchgeführt)

Auslöser: Nutzer bemerkte beim Durchsehen berechneter Routen, dass Abschnitte nicht immer
asphaltiert sind (Beispielroute im Köpenicker Forstgebiet, wo es viele unbefestigte
Wald-/Wirtschaftswege gibt). GraphHopper hatte `surface` bereits als Encoded Value in der Config
aktiv (`deploy/graphhopper-config.yml`), wurde aber nie tatsächlich abgefragt — musste also nicht
in der eigenen OSM-Korridor-Extraktion nachgebaut werden, sondern nur per GraphHopper Path
Details mitgeholt werden. **Stolperstein dabei:** der Query-Parameter heißt `details=surface`,
nicht `path_details=surface` (falscher Name wird von GraphHopper still ignoriert, kein Fehler,
einfach eine leere `details`-Antwort — nur über direktes Nachlesen der GraphHopper-API-Doku
gefunden). Zusätzlich musste der lokale GraphHopper-Graph-Cache einmalig gelöscht und neu
importiert werden, da er vor der `surface`-Konfiguration gebaut worden war (Encoded Values werden
beim Import fest in den Cache geschrieben, eine reine Config-Änderung reicht nicht).

`GraphHopperClient` fragt jetzt `details=surface` auf jeder `/route`-Anfrage mit an und parst die
`[vonIndex, bisIndex, wert]`-Tripel aus der Antwort zu `SurfaceSegment`-Objekten (Domain-Modell,
analog zu `RouteSegment`, aber deckt die GESAMTE Route lückenlos ab statt nur die
Trainings-Intervalle). Frontend zeigt unbefestigte Abschnitte als breiten, halbtransparenten
roten "Halo" unter der normalen blauen Routenlinie an (`MapView.tsx`, Layer
`surface-warning-line`, unter `route-line` einsortiert).

**Bewusst eine Denyliste statt einer Erlaubnisliste** für "unbefestigt": OSM-Straßen ohne
explizites `surface`-Tag liefert GraphHopper als `"missing"` — das betrifft einen großen Teil
aller ganz normalen Asphaltstraßen (Tag wird oft weggelassen, wenn der Belag durch den
Straßentyp schon impliziert ist), waehrend `surface=unpaved`/`gravel`/`dirt`/... fast immer
explizit gesetzt wird, gerade weil es die Ausnahme ist. Eine Erlaubnisliste hätte also
grossflächig falsch-positiv markiert; verifiziert an echten GraphHopper-Antworten (`missing`
kam mit Abstand am häufigsten vor).

Getestet: 2 neue Unit-Tests (`GraphHopperClientTests`, gegen einen gefakten `HttpMessageHandler`
mit kanonischer Path-Details-Antwort) plus vollständiger Live-Test gegen echtes GraphHopper +
API + Frontend im echten Chrome (60-Minuten-Route, rote Halo-Segmente an echten
Schotter-/Pflasterabschnitten sichtbar bestätigt).

## 6.9 Phase 2 — Untergrund-Limits statt nur Anzeige (durchgeführt)

Direkter Folgeauftrag zu 6.8: reine Anzeige reicht nicht, unbefestigte Abschnitte sollen aktiv
vermieden werden. `RouteRequest` bekommt zwei neue optionale Felder (`MaxUnpavedSegmentMeters`,
`MaxTotalUnpavedMeters`, beide `null` = kein Limit) - Klassifizierung "unbefestigt" ist jetzt in
`SurfaceClassifier.IsUnpaved` zentralisiert (Domain), von `RouteConstructionService` UND
`MapView.tsx` genutzt (Frontend-Liste muss bei Aenderungen manuell synchron gehalten werden,
siehe Kommentar dort).

GraphHopper bietet keinen harten "vermeide insgesamt X Meter Untergrund Y"-Constraint (das ist
eine Pfad-Aggregat-Eigenschaft, keine Kanten-Gewichtung, die ein Routing-Algorithmus direkt
optimieren kann). Stattdessen: `RouteConstructionService.BuildRouteAsync` probiert bei gesetztem
Limit **immer alle 5** kompletten Routen-Varianten durch (`round_trip.seed` 1..5, jeweils inkl.
der kompletten bestehenden Hoehenprofil-Verfeinerung und Korridor-Splicing-Pipeline aus 6.2) und
nimmt die mit dem geringsten unbefestigten Gesamtanteil - bewusst NICHT die erste, die die
Grenzwerte unterschreitet, sonst bliebe ein spaeterer, deutlich besserer Versuch ungenutzt. Haelt
selbst der beste Versuch die Grenzen nicht ein, wird er trotzdem verwendet, aber mit
transparenter Warnung statt einer falschen Erfolgsmeldung - **keine Garantie**, nur ein
bestmoeglicher Versuch, analog zur bestehenden Korridor-Fallback-Eskalation aus 4.3. Ohne
gesetztes Limit genau ein Versuch wie vorher, keine zusaetzlichen GraphHopper-Anfragen.

Frontend: zwei neue Zahlenfelder ("kein Limit" als Platzhalter bei leerem Feld = `null`).

Getestet: 5 Unit-Tests gegen einen Fake-GraphHopper-Client, dessen zurückgegebene
Untergrund-Segmente vom angefragten Seed abhängen (kein Limit → ein Versuch; ein früher Seed
erfüllt das Limit bereits, ein späterer ist aber klar besser → alle 5 Versuche laufen trotzdem,
der bessere wird gewählt, nicht der erste passende; Limit nie erreicht → bester von 5 Versuchen
plus Warnung; Segment- und Gesamt-Limit unabhängig voneinander geprüft). Zusätzlich live gegen
echtes GraphHopper verifiziert: strenges Limit (100 m/50 m) führte zu allen 5 Versuchen und der
korrekten Warnung, großzügiges Limit (3000 m) fand eine passende Variante ohne Warnung.

## 6.10 Phase 2 — Für Radfahrer gesperrte Straßen ausschließen (durchgeführt)

Auslöser: Nutzer fragte, ob OSM Daten dazu liefert, dass manche Straßen (Beispiel: B1 östlich von
Berlin) für Radfahrer gar nicht befahren werden dürfen. **Ja, OSM hat das** (`bicycle=no`,
`access=no`, `motorroad=yes`) - aber unser GraphHopper-Profil hat es bisher **nicht ausgewertet**.
`graph.encoded_values` enthielt nur `road_class,surface,road_environment,max_speed,average_slope`,
und das `custom_model` schloss ausschließlich `road_class == MOTORWAY` aus. Ueber `/info` direkt
am laufenden GraphHopper geprüft: es gab keine `bike_access`-Encoded-Value, nur automatisch
mitgelieferte `car_access` - GraphHopper hatte also schlicht keine gespeicherte Information
darüber, ob ein Weg für Fahrräder gesperrt ist.

**Konkret verifiziert statt nur vermutet:** Per Overpass-API-Abfrage eine echte, aktuell gesperrte
B1-Teilstrecke gefunden ("Alte Berliner Straße", `highway=primary` + `bicycle=no`, NICHT einmal
`trunk` - road_class-Filterung allein hätte das also so oder so nie erfasst). Route zwischen den
beiden Enden dieser ca. 300m-Teilstrecke VOR dem Fix: GraphHopper fährt direkt durch
(`road_class` durchgängig `primary`, 302m). NACH dem Fix (siehe unten): 332m über einen legalen
Umweg (`unclassified`/`path`/`footway`), die gesperrte Straße wird korrekt gemieden.

**Fix:** `bike_access` zu `graph.encoded_values` hinzugefügt (`deploy/graphhopper-config.yml` und
`phase0-spike/graphhopper/config.yml`) und im `custom_model` eine zusätzliche Prioritäts-Regel
`if: "!bike_access", multiply_by: "0"` ergänzt - reine GraphHopper-Config-Änderung, kein C#-Code
betroffen, da GraphHopper gesperrte Wege dadurch gar nicht mehr als Routing-Optionen anbietet.
Encoded Values werden beim Graph-Import fest geschrieben (siehe 6.8) - lokaler Graph-Cache musste
daher einmalig gelöscht und neu importiert werden. Auf Render nicht nötig, da der Graph bei jedem
Docker-Build ohnehin komplett frisch importiert wird (siehe DEPLOY.md).

## 7. Offene Punkte

- Kalibrierung der genauen Score-Gewichte und Zonen-Schwellwerte (aktuell Platzhalter-Werte,
  brauchen echte Trainingsfahrten zur Kalibrierung — explizit Teil von Phase 3, nicht vorher
  lösbar)
- Spatial Index für die Korridorsuche, falls Regionsgröße/Anfragevolumen den linearen Scan zum
  Flaschenhals machen (Performance-Optimierung, aktuell kein akutes Problem)
- Garmin.FIT.Sdk 21.214.0 `wkt_step_name`-Dekodierfehler bei gemischten benannten/unbenannten
  Schritten (siehe 6.2) - liegt in der Drittanbieter-SDK, nicht selbst behebbar; betrifft nur
  Anzeige-Labels, nicht die eigentliche Routenplanung
