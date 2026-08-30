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

- **Mehrbenutzerfähigkeit/Auth/Vereine** (geplant, noch nicht umgesetzt) - Reihenfolge laut
  Nutzer: (1) Nutzerkonten mit Login, (2) Vereine mit Mitgliedschaft und Rollen
  (normales Mitglied vs. "Verantwortlicher"), als Grundlage für ein mögliches späteres
  Preismodell, (3) Migration der Sperr-Bereiche (6.18) von rein Frontend-Request-State zu
  persistierten, pro-Nutzer ODER pro-Verein geltenden Ressourcen - eine Verein-weite Sperrung
  muss dabei von einem Verantwortlichen freigegeben werden (Status pending/approved), eine
  Nutzer-eigene Sperrung gilt sofort.

  **Auth-Entscheidung (2026-08-30):** ASP.NET Core Identity (selbst gehostet, in der
  bestehenden API), fuer den ersten Wurf NUR klassische E-Mail/Passwort-Registrierung
  (Passwort-Hashing, Reset-Flow etc. als eigenes Subsystem) - Google-, Microsoft- UND
  Apple-Login wurden bewusst zurueckgestellt, um die erste Version kleiner zu halten (Apple
  zusaetzlich, weil es einen kostenpflichtigen Apple Developer Account voraussetzt). Diskutierte
  Alternativen: ein selbst gehostetes Keycloak (passt gut zum "kostenlos/selbst gehostet"-Muster
  des Projekts, aber ein weiterer zu betreibender Dienst mit eigener DB/eigenem Deployment) und
  ein SaaS-Anbieter wie Auth0/Clerk (weniger Code, aber wiederkehrende Kosten ab einer gewissen
  Nutzerzahl UND nimmt das Vereins-Rollenmodell ohnehin nicht ab). Keycloak bleibt bewusst als
  spaetere Option im Hinterkopf, falls der Funktionsumfang (mehr Provider, MFA, mehrere
  Client-Apps mit gemeinsamem Login) den Mehraufwand rechtfertigt.

  **DB-Entscheidung (2026-08-30):** Postgres bei **Neon.tech** (kostenloser Tier). Die App hat
  aktuell KEINERLEI Datenbank/Persistenz (alles zustandslos pro Anfrage, PBF-Daten einmalig beim
  Start ins RAM geladen) - das ist der erste konkrete Umsetzungsschritt. Verworfene
  Alternativen: Render's eigenes verwaltetes Postgres (kostenlose Variante laeuft nach 30 Tagen
  ab, danach 14 Tage Gnadenfrist, dann Loeschung - fuer echte Nutzerdaten ungeeignet; bezahlt ab
  7$/Monat); ein selbst gehosteter Postgres- ODER SQLite-Container auf Render (Render's
  Dateisystem ist fluechtig, ein persistenter Zustand braucht zwingend einen bezahlten Disk -
  kostet aehnlich wie Render's verwaltetes Postgres, aber ohne automatische Backups, und ein
  Disk bindet den Dienst dauerhaft an eine einzelne Instanz, verhindert also jede spaetere
  horizontale Skalierung).

  **Status (2026-08-30):** Neon-Projekt "WattLoop" angelegt und per Neon-CLI verknuepft
  (`neon link`), neues Projekt `TrainingRoutePlanner.Data` mit `WattLoopDbContext` (ASP.NET
  Core Identity Standard-Schema) angelegt, `DATABASE_URL` (Neon-URI) wird zur Laufzeit in einen
  Npgsql-Verbindungsstring uebersetzt (lokal aus `.env.local`, auf Render direkt als echte
  Umgebungsvariable). Initiale Migration erstellt und live gegen die echte Neon-DB angewendet,
  Tabellen per `neon psql` verifiziert.

  **Registrierung/Login (2026-08-30, durchgeführt):** `POST /auth/register`, `POST /auth/login`,
  `GET /auth/me` (geschuetzt). Bearer-Token (JWT, HMAC-SHA256, 30 Tage Gueltigkeit, bewusst ohne
  Refresh-Token-Mechanismus - fuer Stufe 1 ausreichend) statt Cookies, da Frontend und API auf
  unterschiedlichen Origins laufen und Cross-Site-Cookies (SameSite=None + Domain-Handling)
  unnoetig kompliziert waeren. Signierschluessel lokal per `dotnet user-secrets`, auf Render
  spaeter per Umgebungsvariable `Jwt__SigningKey` (noch nicht gesetzt - Deployment dieser
  Funktion steht noch aus). `RequireUniqueEmail` aktiviert (Identity's Default ist aus, waere bei
  E-Mail-basiertem Login aber mehrdeutig). Identity's Standard-Passwortrichtlinie unveraendert
  uebernommen. E-Mail-Bestaetigung/Passwort-Reset-Flow bewusst noch nicht umgesetzt (kein
  E-Mail-Versand-Setup vorhanden) - eigener spaeterer Schritt.

  Live gegen die echte Neon-DB verifiziert: Registrierung, doppelte E-Mail korrekt abgelehnt
  (400), falsches Passwort korrekt abgelehnt (401), gueltiges Login liefert Token, `/auth/me`
  ohne Token 401/mit Token 200, zu schwaches Passwort von Identity's Regel abgelehnt (400).
  Dabei einmal live beobachtet: Neons serverless Compute war zwischenzeitlich in den
  Ruhezustand (Scale-to-Zero) gegangen - der erste Request danach lief in einen
  Verbindungs-Timeout, ein Retry weckte die Datenbank und lief danach normal durch (kein Bug,
  erwartetes Neon-Verhalten bei Inaktivität).

  **Login-UI (2026-08-30, durchgeführt):** Neue `AuthPanel`-Komponente oben in der Sidebar
  (E-Mail/Passwort, Anmelden/Registrieren, bzw. "Angemeldet als ..."/Abmelden). Token liegt im
  `localStorage` und wird beim Laden gegen `/auth/me` validiert (verhindert stillschweigendes
  Vertrauen in ein abgelaufenes Token, ohne bei jedem Reload einen erneuten Login zu erzwingen).
  Bewusst KEIN hartes Login-Gate - solange Stufe 3 (persistierte, pro-Nutzer geltende
  Sperr-Bereiche) nicht existiert, gibt es nichts, das ein eingeloggter Zustand tatsaechlich
  freischalten wuerde, die Routenplanung bleibt fuer alle nutzbar. Live im echten Chrome
  bestaetigt: Registrierung + Auto-Login, Session uebersteht einen Reload, Abmelden, erneutes
  Login mit bestehenden Zugangsdaten, und die Fehleranzeige bei falschem Passwort.
- A-nach-B-Routing (statt nur Rundkurs)
- Grundlegender Umbau: die GESAMTE Rundstrecke (nicht nur Intervall-Segmente wie bisher) aus
  aneinandergereihten, niedrig bewerteten Korridoren verketten statt GraphHoppers round_trip als
  Basis zu nutzen - naeher an der urspruenglichen Konzept-Idee als der leichtgewichtige
  Retry-Ansatz aus 6.13, aber ein deutlich groesserer algorithmischer Umbau mit entsprechendem
  Risiko (siehe 6.13 fuer die Diskussion, warum zunaechst der kleinere Ansatz gewaehlt wurde)
- Radweg-/Infrastruktur-Praesenz (`cycleway=*`/`bicycle=use_sidepath`) als Routing-Signal - beim
  Vergleich Hultschiner Damm vs. Wegendorfer Chaussee aufgefallen (siehe 6.13), vom Nutzer aber
  fuer TRAINING als nachrangig gegenueber "wenig Unterbrechungen" eingestuft
- Landstrassen mit niedrigerem Tempolimit bevorzugen, wenn der Untergrund gut ist (weniger/
  langsamerer Durchgangsverkehr auf ungeschuetzten Strecken) - vom Nutzer vorgeschlagen, noch
  nicht umgesetzt
- Feinere Abstufung von `urban_density` als GraphHoppers 3 fixe Stufen (RURAL/RESIDENTIAL/CITY) -
  vom Nutzer als Frage aufgeworfen (z. B. sollte das Sportforum als dichter besiedelt gelten als
  Altlandsberg, auch wenn beide nicht CITY sind). Braeuchte entweder OSM `place=*`-Tags
  (village/town/city) als zusaetzliches Signal oder eine eigene Dichte-Berechnung - GraphHopper
  selbst bietet keine feinere Abstufung an. Noch nicht umgesetzt.
- Zwei Startpunkte fuer gemeinsames Training mehrerer Sportler - offene Design-Frage, ob ein
  gemeinsamer Treffpunkt auf der Strecke berechnet wird (jeder faehrt von seinem Start dorthin,
  dann gemeinsame Runde) oder beide einfach ihre eigene Anfahrt zur selben Rundstrecke bekommen
- Integration von Baustellen (aktuelle Straßensperrungen/-einschränkungen in die Korridor-/
  Routenbewertung einbeziehen, z. B. über OSM `construction`-Tags oder externe Baustellen-Feeds)
- Nutzer können Segmente selbst bewerten/sperren (z. B. "diese Straße meiden" dauerhaft im Profil
  hinterlegen, unabhängig vom automatischen Score aus 3.4)
- Straßenauslastung/Verkehr zur geplanten Trainingsuhrzeit in die Streckenbewertung einbeziehen
  (Nutzer gibt an, wann trainiert werden soll; stark befahrene Straßen zu der Uhrzeit werden
  gemieden). OSM selbst führt keine Verkehrsvolumen-/Stauzeitdaten. Geprüfte Optionen:
  - TomTom/HERE Traffic: kostenpflichtig, Lizenzbedingungen erlauben meist nur Anzeige in einem
    eigenen Kartenkontext, keine Weiterverarbeitung in einem eigenen Scoring-Algorithmus
  - Google Maps Directions/Routes API (`departure_time`-Parameter): technisch möglich, aber aus
    denselben Lizenzgründen abgelehnt, plus kostenpflichtig pro Anfrage - unser
    Untergrund-Vermeidungs-Retry (6.9) macht schon bis zu 5 GraphHopper-Anfragen pro Route, das
    würde sich mit einer bezahlten API schnell summieren
  - **BASt (Bundesanstalt für Straßenwesen):** kostenlose, offizielle stündliche
    Verkehrszähldaten (Datenlizenz Deutschland Namensnennung 2.0) von ca. 2000 Dauerzählstellen -
    aber nur für Autobahnen und Bundesstraßen. Deckt sich kaum mit den kleineren Straßen, auf
    denen unser Korridor-Scoring (3.4) ohnehin bevorzugt routet - vermutlich zu geringe
    Abdeckung für unseren tatsächlichen Anwendungsfall, daher nicht umgesetzt
  - OpenTraffic (World Bank/Mapzen/Conveyal, 2015-2017): offenbar seit 2017 nicht mehr
    weiterentwickelt (Mapzen als Trägerorganisation 2018 eingestellt), keine Hinweise auf einen
    aktuell laufenden Datenfeed - ohnehin auf Entwicklungsländer ohne kommerzielle Verkehrsdaten
    ausgerichtet, vermutlich keine Berlin/Brandenburg-Abdeckung
  - Ersatzweise: grobe Heuristik über Straßenklasse + Tageszeit ohne jede externe Datenquelle -
    kostenlos, aber deutlich ungenauer als echte Zähldaten
  - Teilweise durch 6.11 (städtische Ballungsgebiete meiden) bereits indirekt adressiert, da
    hohe Verkehrsdichte und `urban_density == CITY` stark korrelieren dürften
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

## 6.11 Phase 2 — Städtische Ballungsgebiete meiden (durchgeführt)

Auslöser: Nutzer fragte nach Verkehrsvermeidung generell (Abschnitt 7 dokumentiert die
BASt/Google-Maps-Recherche dazu - beides verworfen, siehe dort) und konkret danach, ob wir
"die Umgebung" prüfen können, um Strecken durch die Stadt zu vermeiden. GraphHopper hat dafür
bereits eine eingebaute Funktion: die `urban_density`-Encoded-Value (`RURAL`/`RESIDENTIAL`/`CITY`)
klassifiziert Wege automatisch anhand der Straßen-/Kreuzungsdichte aus den OSM-Daten selbst -
kein eigenes Parsen von Landnutzungs-Polygonen nötig, gleiches Muster wie `surface` (6.8) und
`bike_access` (6.10).

Nutzer entschied sich für **fest im Profil verankert statt konfigurierbar** (anders als die
Untergrund-Limits aus 6.9): `custom_model` wertet `urban_density == CITY` jetzt mit
`multiply_by: "0.5"` ab - bewusst eine Abwertung, kein Ausschluss (`multiply_by: "0"`), da der
Startpunkt (Sportforum Berlin) selbst in einem Stadtrandgebiet liegt und ein hartes Verbot die
Routenerstellung dort unmöglich machen könnte.

**Stolperstein beim Aktivieren:** `graph.urban_density.threads` hat keinen brauchbaren Default -
ohne expliziten Wert (>= 1) stürzt der komplette Import mit `IllegalArgumentException` in
`ForkJoinPool`-Konstruktion ab (`threads=0` ist ungültige Parallelität). In beiden Config-Dateien
`graph.urban_density.threads: 4` ergänzt.

**Weiterer wichtiger Fund:** Die City-Klassifizierung (`city_radius=1500m` Default) ist spürbar
langsam - beim lokalen Reimport hat sie allein ca. 5,5 Minuten gebraucht (bei ~1,5 Mio. Knoten in
unserem 60-km-Extrakt), zusätzlich zur sonstigen Importzeit. Das passiert beim GraphHopper-
Container-**Start** (blockiert `/health` so lange), nicht beim Docker-Build - siehe DEPLOY.md für
den Hinweis zur Render-Health-Check-Grace-Period.

Verifiziert: `/info` zeigt `urban_density` mit den erwarteten drei Werten. Eine Testroute mitten
in Berlin-Mitte (Alexanderplatz-Gegend) wird korrekt durchgängig als `city` klassifiziert
(bestätigt, dass die Klassifizierung tatsächlich greift). Die Runde ab Sportforum Berlin bleibt
bereits ohne jede Anpassung komplett im `rural`-Bereich - kein Widerspruch, da der Standort selbst
am Stadtrand liegt und die Route von dort aus nie in dichtere Gebiete musste; die
Abwertungs-Regel wirkt also erst, wenn eine Route tatsächlich versucht, durch dichter besiedeltes
Gebiet zu führen.

## 6.12 Phase 2 — Untergrund-Vermeidung: echter Produktions-Fund und Fix (durchgeführt)

Nutzer meldete zwei Beobachtungen bei einem realen Test mit sehr strengen Grenzwerten
(`MaxUnpavedSegmentMeters=5`, `MaxTotalUnpavedMeters=10`, 120-min-GA1-Plan, 53 km): (1) die
verwendete Route hatte trotzdem viel unbefestigten Untergrund (4831 m) und (2) der GPX-Download
schien nicht mehr zu funktionieren. Direkt gegen die Live-Render-Instanz reproduziert statt nur
lokal vermutet:

- Eine EINZELNE Routenberechnung ganz ohne Limit brauchte auf Render 15-33 s (warm/kalt) - lokal
  nur ca. 3 s. Ursache dafür nicht abschließend geklärt (vermutlich geteilte/gedrosselte CPU auf
  dem "Standard"-Plan plus die private Netzwerkanbindung zu GraphHopper), aber klar gemessen.
- Der Untergrund-Vermeidungs-Loop aus 6.9 lief bei den o.g. (praktisch unerfüllbaren) Limits
  IMMER alle 5 Versuche durch (da "immer den besten von allen nehmen" aus einer vorherigen
  Iteration bewusst nie vorzeitig abbricht) - 5 × 15-33 s ergibt genau die Größenordnung, bei der
  sowohl der JSON- als auch der GPX-Request (der die Berechnung komplett neu anstößt statt das
  JSON-Ergebnis wiederzuverwenden) live nachweislich in ein komplettes Timeout liefen (>90 s ohne
  jede Antwort, curl-Test gegen die Live-URL).

**Fix, mehrteilig:**
- `RouteConstructionService.BuildRouteAsync` bricht jetzt wieder beim ERSTEN Versuch ab, der die
  Grenzwerte einhält (Rückkehr von "immer alle durchrechnen" aus 6.9 - jener Ansatz war zwar
  gründlicher, aber genau die hier gefundene Ursache des Timeouts). `MaxSurfaceAvoidanceAttempts`
  gleichzeitig von 5 auf 10 erhöht (schadet jetzt nicht mehr, da der Normalfall dank der
  folgenden Profiländerung meist schon beim ersten Versuch durchkommt) und ein hartes
  Zeitbudget (`MaxSurfaceAvoidanceTimeBudget`, 45 s) als Sicherheitsnetz ergänzt - bricht auch
  bei unrealistisch strengen Nutzer-Limits nach spätestens 45 s ab und liefert den bisher besten
  Versuch mit Warnung, statt endlos weiterzuprobieren.
- GraphHopper-Profil bewertet unbefestigte `surface`-Werte jetzt direkt mit `multiply_by: 0.5` ab
  (analog zu MOTORWAY/`bike_access`/`urban_density`) - macht schon den ERSTEN Versuch deutlich
  wahrscheinlicher konform, statt rein auf zufällig günstige round_trip-Seeds zu hoffen. Gleiche
  Denyliste-Logik wie `SurfaceClassifier.IsUnpaved` (Domain), aus GraphHoppers eigenem
  (gröberen) `surface`-Enum nachgebildet.

**Warum 5 m/10 m als Limit ohnehin nie erfüllbar gewesen wären:** so strenge Werte verbieten
praktisch jede noch so kurze unbefestigte Kreuzung/Seitenstreifen - realistisch nutzbare Werte
liegen eher im drei- bis vierstelligen Meterbereich. Das ändert nichts an der Notwendigkeit des
Zeitbudgets (ein Nutzer kann jederzeit versehentlich einen unerfüllbaren Wert eintragen), macht
aber verständlich, warum ausgerechnet dieser Testfall beide Probleme gleichzeitig aufgedeckt hat.

Getestet: bestehende Unit-Tests auf frühzeitigen Abbruch zurückgeführt (statt "immer alle
Seeds"), neuer Test für "alle 10 Versuche ausgeschöpft, bester bei Gleichstand gewinnt". Der
Zeitbudget-Pfad selbst ist bewusst nicht unit-getestet (bräuchte echte Verzögerungen oder eine
Testbarkeits-Naht einzig für diesen Zweck) - Vertrauen kommt aus der einfachen, leicht
nachvollziehbaren `Stopwatch`-Prüfung und der Live-Verifikation gegen Render.

## 6.13 Phase 2 — Ampeln/Kreuzungen für die GESAMTE Route vermeiden (durchgeführt)

Nutzer fragte, ob wir Kreuzungen/Ampeln in die Routenkriterien einbeziehen können. Der
Unterbrechungs-Score aus 3.4 (Ampeln, Stopp, Kreisverkehr, Rechts-vor-links) existiert bereits
seit Phase 0/1, wurde aber bisher NUR zur Korridor-Auswahl für Intensitäts-Intervalle genutzt,
nicht für die restliche/ruhige Strecke oder die Grundform der Runde. GraphHopper selbst bietet
dafür nichts Fertiges (anders als bei Untergrund/Zugang/Stadtgebiet) - es gibt sogar einen
offenen, unimplementierten Vorschlag für genau sowas
([Crossing-Encoded-Values fürs Fußgänger-Routing](https://github.com/graphhopper/graphhopper/issues/2932)).

Nutzer wählte den Ansatz "nachträglicher Check + Retry" (wie 6.9/6.12) statt eines grundlegenden
Umbaus zu vollständiger Korridor-Verkettung für die Gesamtroute (letzteres bleibt als größeres,
separates Vorhaben in Abschnitt 7 vorgemerkt).

**Umsetzung:** `CorridorIndex` bekommt ein einfaches Bucket-Gitter über `RoadGraph.HardNodes`
(Ampeln/Stopp, ~200m-Zellen, 3x3-Nachbarschaftssuche) und eine neue Methode
`CountDisruptiveJunctionsNear(routeGeometry, proximityMeters)`, die zählt, wie viele
UNTERSCHIEDLICHE Ampel-/Stopp-Knoten innerhalb von 25m irgendeines Punktes der fertigen Route
liegen (nicht die volle Score-Funktion aus 3.4 - die braucht gerichteten Vorgänger/Nachfolger-
Kontext im Graphen, den ein beliebiger Routen-Polylinienpunkt nicht hat). `RouteRequest.
MaxDisruptiveJunctions` (null = kein Limit) nutzt denselben Retry-Loop wie die
Untergrund-Limits aus 6.12 - dieselbe Zeitbudget-Absicherung gilt also automatisch mit. Der
Fallback-Vergleich bei mehreren gleichzeitig gesetzten Limits nutzt eine grobe Heuristik
("eine Kreuzung vermeiden ≈ 300m unbefestigten Untergrund vermeiden") nur für die Auswahl des
bestmöglichen Kompromisses, nicht für die Prüfung der tatsächlichen Nutzer-Grenzwerte selbst.

**Verworfene Nebenspur:** Beim Vergleich zweier realer Beispielstraßen (Hultschiner Damm vs.
Wegendorfer Chaussee) fiel auf, dass `bicycle=use_sidepath`/`cycleway=separate` (eigener
Radweg) ein interessantes zusätzliches Signal wäre - Nutzer stellte aber klar, dass für
strukturiertes TRAINING wenig Unterbrechungen wichtiger ist als ein separater Radweg (eine
ruhige Landstraße ohne Radweg schlägt eine belebtere Straße mit Radweg). Bleibt als mögliche
spätere Idee vorgemerkt, aber nicht Teil dieser Umsetzung.

Getestet: 2 neue Unit-Tests für `CountDisruptiveJunctionsNear` (zaehlt unterschiedliche Knoten
nur einmal trotz mehrfacher Naehe, ignoriert weit entfernte Knoten), 1 neuer Test für die
Verdrahtung in `RouteConstructionService`. Live gegen echtes GraphHopper verifiziert: strenges
Limit (3 Kreuzungen) lief korrekt durch alle 10 Versuche und warnte mit Klartext-Zahlen (beste
gefundene Variante: 36 Kreuzungen), großzügiges Limit (40) fand sofort eine passende Variante.

## 6.14 Phase 2 — Bots/Suchmaschinen aussperren (durchgeführt)

Nutzer wollte die (bewusst auth-lose, siehe DEPLOY.md) App vor Suchmaschinen-Indexierung und
Bot-Traffic schützen. `frontend/public/robots.txt` (Disallow für alle) plus `<meta
name="robots" content="noindex, nofollow, noarchive, nosnippet">` in `index.html`. API bekommt
zusätzlich einen `X-Robots-Tag`-Header auf jede Antwort sowie eine einfache
User-Agent-Substring-Sperre (403) gegen bekannte Crawler/Scraper (Google/Bing/Yandex, SEO-Bots
wie Ahrefs/Semrush, KI-Crawler wie GPTBot/ClaudeBot/CCBot). **Kein Ersatz für echten
Bot-/Rate-Limit-Schutz** (WAF/Cloudflare wären das) - erwischt nur Bots, die sich ehrlich per
User-Agent identifizieren, hält aber die grosse Mehrheit der bekannten, gutartigen Crawler fern.

## 6.15 Phase 2 — Anzahl Streckenvarianten konfigurierbar (durchgeführt)

Nutzer wollte die feste Anzahl von Routen-Varianten aus 6.9/6.12/6.13 (bisher hart auf 10
kodiert) selbst einstellen können. `RouteRequest.MaxRouteVariantAttempts` (null = Standardwert
10) überschreibt die Versuchszahl im Retry-Loop von `RouteConstructionService` - wirkungslos,
wenn keines der drei Limits (Untergrund/Kreuzungen) gesetzt ist, da dann ohnehin nur ein
einziger Versuch läuft. Das feste 45s-Zeitbudget aus 6.12 bleibt unabhängig davon als
Sicherheitsnetz bestehen, auch bei einem hoch gesetzten Wert.

Getestet: neuer Unit-Test (Limit auf 3 gesetzt, bricht nach genau 3 statt der sonst üblichen 10
Versuchen ab), live gegen echtes GraphHopper verifiziert (Warnung nennt korrekt "3 probierten
Streckenvarianten" statt der Standard-Formulierung mit 10).

## 6.16 Phase 2 — highway=track ausschließen (durchgeführt)

Nutzer fragte konkret nach einer Koordinate (52.58948, 13.71823), an der eine Route lang zu
führen schien - per Overpass-Abfrage geprüft: ein `highway=track` **komplett ohne weitere Tags**
(kein Name, kein Untergrund, keine Klassifizierung) - praktisch ein unbefestigter Feld-/
Waldweg. Unsere eigene Korridor-Extraktion (`HighwayTags.RoadHighwayTypes`) schließt `track`
schon seit Phase 0/1 aus, das GraphHopper-Profil selbst aber nicht - und da `track`-Wege fast
nie ein `surface`-Tag tragen, greift auch die Untergrund-Abwertung aus 6.12 nicht ("missing"
wird dort bewusst nicht als unbefestigt gewertet, siehe SurfaceClassifier - das gilt fuer echte
Strassen, aber nicht fuer track).

Nutzer entschied sich für kompletten Ausschluss (`road_class == TRACK, multiply_by: "0"`,
analog zu MOTORWAY) statt einer Abwertung - konsistent damit, dass unsere eigene Korridor-Logik
track ohnehin nie für Training vorschlägt.

**Wichtiger operativer Fund dabei:** GraphHopper hasht das `custom_model` gegen den
Graph-Cache und verweigert den Start bei jeder Änderung ("Profiles do not match") - ein reiner
Neustart reicht bei `custom_model`-Änderungen NIE, es braucht immer einen kompletten Reimport
(auch wenn wie hier keine neue Encoded Value hinzukam, nur eine neue Regel mit einer bereits
aktiven). Betrifft nur lokale Entwicklung - auf Render importiert der Container ohnehin bei
jedem Deploy frisch (siehe DEPLOY.md).

Verifiziert: direkte Route zwischen den beiden Enden des gemeldeten Track-Wegs nimmt jetzt
einen ca. 2,3 km-Umweg über `path`/`residential`/`unclassified` statt der direkten ~850 m über
`track`.

## 6.17 Phase 2 — GraphHoppers "racingbike"-Template als Profil-Fundament (durchgeführt)

Nutzer fragte, ob man GraphHopper direkt mitteilen kann, dass ein Rennrad geroutet werden soll.
GraphHopper hat dafür ein fertiges, mitgeliefertes Custom-Model-Template
(`core/src/main/resources/com/graphhopper/custom_models/racingbike.json`) - deutlich
ausgefeilter als unser bisher komplett selbst gebautes Profil:

```json
{
  "priority": [
    { "if": "true",  "multiply_by": "racingbike_priority" },
    { "if": "bike_network == INTERNATIONAL || bike_network == NATIONAL",  "multiply_by": "1.2" },
    { "else_if": "bike_network == REGIONAL || bike_network == LOCAL",  "multiply_by": "1.1" },
    { "if": "road_environment == FERRY", "multiply_by": "0.5" },
    { "if": "mtb_rating > 2",  "multiply_by": "0" },
    { "else_if": "mtb_rating == 2",  "multiply_by": "0.5" },
    { "if": "hike_rating > 1",  "multiply_by": "0" },
    { "if": "bike_road_access == NO", "multiply_by": "0" },
    { "if": "!racingbike_access && (!backward_racingbike_access || roundabout)",  "multiply_by": "0" },
    { "else_if": "!racingbike_access && backward_racingbike_access",  "multiply_by": "0.2" }
  ]
}
```

`racingbike_priority` ist ein von GraphHopper selbst aus Straßentyp/Oberfläche/etc. berechneter
Gesamt-Eignungswert pro Weg (numerisch, in Stichproben zwischen 0.9 und 1.3 beobachtet) -
`racingbike_access`/`mtb_rating`/`hike_rating`/`bike_road_access` decken Zugangs- und
Technik-Einschränkungen ab, die wir bisher gar nicht kannten. `bike_network` bevorzugt sogar
offiziell ausgewiesene Radrouten. Wortgleich übernommen als Fundament, unsere bisherigen
Ergänzungen (MOTORWAY/TRACK-Ausschluss, `bike_access`, `urban_density`-Abwertung,
Untergrund-Denyliste) bleiben bewusst redundant obendrauf bestehen - schadet nicht, schützt aber
falls das Template einen Fall doch nicht abdeckt (z. B. `bike_access`/`racingbike_access` wurden
nicht auf Gleichwertigkeit geprüft, bevor eine der beiden Regeln entfernt würde).

**Bewusst NICHT übernommen:** Das Template hat auch eigene `speed`-Regeln
(`racingbike_average_speed`). Unsere Zeitschätzung hängt am eigenen physikbasierten
`PowerSpeedModel` (FTP/Gewicht/Zonenleistung), das GraphHoppers zurückgegebene `time` für die
Streckenlänge je Trainingsschritt nutzt - ein variables GraphHopper-eigenes
Geschwindigkeitsmodell würde diese Schätzung verfälschen bzw. inkonsistent zur angezeigten
"Geschätzte Zeit" machen. Der bisherige feste `limit_to: 25`-Wert bleibt daher unverändert.

Neue Encoded Values: `racingbike_access, racingbike_average_speed, racingbike_priority,
bike_network, mtb_rating, hike_rating, bike_road_access` - kompletter Reimport nötig (siehe
6.16 zum Hash-Check).

Verifiziert: `/info` zeigt alle neuen Encoded Values mit plausiblen Werten. Keine Regression bei
den vorherigen Fixes (B1 `bicycle=no` weiterhin gemieden, Berlin-Mitte weiterhin korrekt als
`city` erkannt, `highway=track` weiterhin gemieden). Voller Testlauf (58/58) und ein
End-to-End-Routen-Test über die lokale API ohne Warnungen bestätigt.

## 6.18 Phase 2 — Streckenabschnitte auf der Karte sperren (durchgeführt)

Nutzer wollte gezielt einzelne Streckenabschnitte sperren können, per Klick auf der Karte mit
einer Auswahl (Startpunkt setzen ODER Abschnitt sperren) - eskaliert aus der Phase-4-Backlog-Idee
"Nutzer können Segmente selbst bewerten/sperren".

**GraphHoppers klassischer `block_area`-Parameter wurde entfernt** (Serverantwort verweist
explizit auf `custom_model` mit `"areas"`). Das erfordert einen **kompletten Umbau von
GraphHopperClient**: von GET mit Query-String auf POST mit JSON-Body, da nur so ein
per-Request-`custom_model` mitgeschickt werden kann. Dabei zwei undokumentierte Stolpersteine
per echtem Test statt Doku-Vertrauen gefunden:
- `round_trip.distance`/`round_trip.seed` sind im JSON-Body **flache Schlüssel mit Punkt im
  Namen** (`"round_trip.distance": 2000`), NICHT wie zunächst angenommen ein verschachteltes
  `{"round_trip": {"distance": ...}}`-Objekt - ohne diese Erkenntnis wurde die Distanz komplett
  ignoriert (angefragte 2000m wurden zu ~8900m).
- Per-Request-`custom_model`-Overrides brauchten bei unserem Profil KEINE zusätzliche
  Server-Freigabe (einfach ausprobiert statt vermutet) - erweitert das Server-Profil um die
  mitgeschickten Regeln, ersetzt es nicht.

**Umsetzung:** `BlockedArea(GeoPoint Center, double RadiusMeters)` in `RouteRequest`. Ein
gesperrter Bereich wird als 12-Ecks-Polygon (Kreis-Näherung, flache Grad/Meter-Umrechnung reicht
für die hier relevanten kleinen Radien) in `custom_model.areas` kodiert, mit einer
Prioritäts-Regel `if: "in_blocked0 || in_blocked1 || ..."`, `multiply_by: "0"` - komplett
ausgeschlossen, keine Abwertung (anders als Stadtgebiet/Untergrund: hier will der Nutzer explizit
NIE durch diesen Bereich fahren). Wird an JEDEN GraphHopper-Aufruf durchgereicht (round_trip UND
Wegpunkt-Routing).

Frontend: Kartenklick zeigt jetzt ein MapLibre-Popup mit zwei Buttons statt direkt den Startpunkt
zu setzen. Gesperrte Bereiche werden als roter Kreis-Layer angezeigt und in einer Liste in der
Sidebar mit "Entfernen"-Button verwaltet. Fester Radius (40m) ohne eigenes UI-Element, um die
Interaktion einfach zu halten.

Getestet: 2 neue Unit-Tests für `GraphHopperClient` (prüfen die exakte JSON-Struktur des
gesendeten Request-Body inkl. geschlossenem Polygon-Ring), volle Testsuite (60/60) nach dem
API-Umbau grün. Live gegen echtes GraphHopper verifiziert: Route mit gesperrtem 40m-Bereich
bleibt exakt 41m vom Zentrum entfernt (vorher führte sie mittendurch) - UI-Interaktion
(Klick → Popup → Sperren → roter Kreis → Sidebar-Eintrag) im echten Chrome bestätigt.

## 6.19 Phase 2 — Pflicht-Wegpunkte auf der Karte erzwingen (durchgeführt)

Ergänzung zu 6.18: Nutzer wollte nicht nur Abschnitte sperren, sondern auch einen bestimmten
Punkt gezielt IN die Route einschließen können - schließt die zweite Hälfte der
Phase-4-Backlog-Idee "Nutzer setzt manuell eigene Wegpunkte ... (umgehen ODER erzwingen)".

**Umsetzung:** `RouteRequest.RequiredPoints` (einfache `GeoPoint`-Liste, kein Radius nötig -
GraphHopper snapped ohnehin auf den nächsten routbaren Weg). In `RouteConstructionService`
werden diese Punkte NICHT einfach an die Korridor-Wegpunkte angehängt, sondern anhand ihrer
nächsten Projektion auf die grobe Rundtour-Form (`PolylineMath.NearestPointDistanceAlongMeters`,
neue Punkt-auf-Segment-Projektion mit derselben flachen Grad/Meter-Näherung wie schon bei den
BlockedArea-Kreisen) in der richtigen Reihenfolge zwischen die Korridor-Start/Ende-Paare
einsortiert - sonst würde ein spät in der Route liegender Pflichtpunkt, der zufällig zuerst in
der Liste steht, einen unnötigen Umweg erzwingen. Ein einzelner Pflicht-Wegpunkt reicht bereits
aus, um `RouteThroughWaypointsAsync` statt des reinen `round_trip`-Ergebnisses zu erzwingen, auch
bei einem sonst rein ruhigen Plan ohne Effort-Schritte.

Frontend: Kartenklick-Popup (6.18) um einen dritten Button "Diesen Punkt in die Route
einschließen" erweitert. Pflicht-Wegpunkte werden als grüner Kreis-Layer angezeigt und in einer
eigenen Sidebar-Liste ("Pflicht-Wegpunkte") verwaltet, analog zu den gesperrten Bereichen.

Getestet: 2 neue Unit-Tests für `RouteConstructionService` (Pflicht-Wegpunkt erzwingt
Wegpunkt-Routing auch ohne Effort-Schritt; Einsortierung nach Position in der Schleife statt nach
Eingabe-Reihenfolge), volle Testsuite grün.

## 6.20 Phase 2 — smoothness=bad wie unbefestigten Untergrund behandeln (durchgeführt)

Beim Nachschauen einer vom Nutzer gemeldeten Koordinate (Birkensteiner Straße, 52.51750,
13.66599) fiel auf: `surface=sett` (bereits als unbefestigt erfasst, siehe 6.9) UND
`smoothness=bad` gemeinsam getaggt. Nutzer wollte `smoothness=bad` als eigenständiges,
zusätzliches Kriterium - deckt auch Fälle ab, wo ein Belag rissig/holprig, aber gar nicht über
`surface` als problematisch getaggt ist (z. B. alter Asphalt ohne eigenes `surface`-Tag).

**Umsetzung:** GraphHopper-`smoothness`-Encoded-Value aktiviert (`graph.encoded_values`), als
zusätzliches `path_details`-Feld angefragt (`GraphHopperClient.Details`). `SurfaceClassifier.
IsBadSmoothness` (Denylist: BAD/VERY_BAD/HORRIBLE/VERY_HORRIBLE/IMPASSABLE) wird in
`RouteConstructionService.EvaluateUnpavedSurfaces` GEMEINSAM mit der bestehenden
Oberflächen-Denylist ausgewertet - beide Anteile werden zur "Badness"/Grenzwert-Bewertung
aufsummiert. Überschneiden sich beide Warnungen auf demselben physischen Abschnitt (surface- und
smoothness-`path_details` nutzen unabhängige Indexbereiche über dieselbe Geometrie), wird das
bewusst doppelt gezählt statt exakt über Geometrie-Indizes verrechnet - bewusst grob wie schon
bei `JunctionBadnessWeightMeters` (6.13), und hier zusätzlich in der sicheren Richtung: eine
Überschätzung kostet höchstens einen unnötigen weiteren Versuch, eine Unterschätzung würde einen
tatsächlich schlechten Abschnitt durch die Nutzer-Grenzwerte rutschen lassen. Auf Profil-Ebene
zusätzlich dieselbe Abwertung (`multiply_by: 0.5`) wie beim Untergrund (6.12) - macht auch hier
schon den ersten Versuch wahrscheinlicher konform.

Getestet: neue Unit-Tests für `GraphHopperClient` (parst `smoothness`-`path_details` korrekt,
fragt beide Details-Keys an) und `RouteConstructionService` (ein reiner smoothness=bad-Abschnitt
ohne jedes surface-Problem löst denselben Retry-Mechanismus aus wie ein unbefestigter
Oberflächen-Abschnitt), volle Testsuite grün.

## 6.21 Phase 2 — Ampeln/Stoppschilder als optionaler Kartenlayer (durchgeführt)

Nutzerwunsch: einen ein-/ausblendbaren Kartenlayer, der Ampeln und Stoppschilder anzeigt.

**Datenlage:** `RoadGraph.HardNodes` (OSM `highway=traffic_signals`/`stop`) enthielt diese
Knoten bereits vollständig - genutzt für `CountDisruptiveJunctionsNear` (6.13) -, aber ohne
Typ-Unterscheidung (beide Tags flossen in dieselbe Menge). Für die Kartenanzeige war das zu
grob, da Ampel und Stoppschild visuell unterschieden werden sollten.

**Umsetzung:** Neues, zu `HardNodes` PARALLELES `RoadGraph.HardNodeTypes`
(`Dictionary<long, HardNodeType>`, `HardNodeType` = `TrafficSignal`/`Stop`) - bewusst
zusätzlich statt `HardNodes` selbst von `HashSet<long>` auf ein typisiertes Dictionary
umzustellen, um die bestehende Score-/Zähl-Logik (`CorridorScoring`,
`CountDisruptiveJunctionsNear`) und ihre 11 bestehenden Testfälle unangetastet zu lassen.
`CorridorIndex.GetAllJunctions()` (neu auf `ICorridorIndex`) liefert alle Punkte mit Typ, neuer
Endpunkt `GET /junctions` gibt sie unauthentifiziert komplett aus (kein Bounding-Box-Filter -
konsistent mit dem Ansatz, die gesamte Region einmalig im Speicher zu halten, siehe 4.1). Live
verifiziert: 9.957 Knoten insgesamt (9.047 Ampeln, 910 Stoppschilder), ~920 KB JSON - für einen
einmaligen Abruf unproblematisch groß.

Frontend: `MapView` fetcht `/junctions` einmalig beim Mounten (nicht bei jedem Ein-/Ausblenden
neu) und cached als MapLibre-GeoJSON-Source; das Ein-/Ausblenden schaltet nur die
`visibility`-Layout-Property des Layers um. Ampeln rot, Stoppschilder orange (`circle-color`
per `match`-Ausdruck auf das `type`-Feld, analog zum bestehenden `segmentColorExpression`-Muster).
Checkbox "Ampeln/Stoppschilder auf der Karte anzeigen" in den Einstellungen. Im echten Chrome
bestätigt: dichtes Ampel-Netz in Berlin sichtbar, Ein-/Ausblenden funktioniert ohne erneuten
Netzwerk-Request.

Getestet: neuer Unit-Test für `CorridorIndex.GetAllJunctions()`, volle Testsuite (66/66) grün.

## 6.22 Phase 2 — Windmodellierung für die Zeitschätzung (durchgeführt)

Scope bewusst auf die Zeitschätzung begrenzt, die Streckenführung selbst bleibt unverändert -
siehe Diskussion vor Umsetzung. Neues optionales Eingabefeld "Geplanter Fahrzeitpunkt" (leer =
keine Windvorhersage, bestehendes Verhalten unverändert).

**Datenquelle:** Open-Meteo (kostenlos, kein API-Key), EIN Abruf pro Routen-Anfrage für
Startpunkt + geplanten Zeitpunkt (nicht pro Trainingsschritt neu) - liefert `null` statt zu
werfen, wenn keine Vorhersage verfügbar ist (z. B. Zeitpunkt zu weit in der Vergangenheit),
Windmodellierung ist rein additiv und darf die Routenberechnung nie verhindern.

**Physik:** `PowerSpeedModel.SolveSpeedMps` berechnet den Luftwiderstand jetzt über die
RELATIVE Windgeschwindigkeit (Bodengeschwindigkeit + Gegenwind-Komponente) statt der reinen
Bodengeschwindigkeit, mit vorzeichenerhaltender `v*|v|`-Form statt `v²` - bei sehr starkem
Rückenwind (Windkomponente betragsmäßig größer als die Bodengeschwindigkeit) kehrt sich die
Windkraft korrekt zum Vortrieb um, statt fälschlich weiter Widerstand zu erzeugen. Newton-
Raphson-Ableitung entsprechend angepasst (Identität d/dv[v·|v|] = 2|v|).

**Windkomponente pro Trainingsschritt:** über denselben iterativen Verfeinerungs-Mechanismus
wie das Höhenprofil (3.3/6.2) bestimmt - neuer `PolylineMath.AverageBearingDegrees`-Helfer
(Peilung zwischen Fenster-Anfang und -Ende, analog zu `AverageGradient`; kein Kompass-
Wraparound-Problem, da `BearingDegrees` bereits einen einzelnen wohldefinierten Wert liefert,
einfacher als ursprünglich vor der Umsetzung angenommen).

**Transparenz:** `RouteResult.Wind` (nullable) trägt die tatsächlich verwendeten
Windbedingungen, im Ergebnis als z. B. "Wind: 12 km/h aus West" angezeigt.

Getestet: neue Unit-Tests für `PowerSpeedModel` (Gegenwind verringert, Rückenwind erhöht die
Geschwindigkeit; Regressionstest für die vorzeichenerhaltende Form bei extremem Rückenwind) und
`RouteConstructionService` (kein Abruf ohne gesetzten Zeitpunkt; genau EIN Abruf trotz mehrerer
Verfeinerungs-Iterationen; korrekter Fallback bei nicht verfügbarer Vorhersage), volle Testsuite
(72/72) grün. Live verifiziert: direkter Open-Meteo-Abruf für Berlin bestätigt die erwartete
Antwortform; ein echter `/route`-Aufruf mit gesetztem Zeitpunkt lieferte einen echten Windwert
(3,21 m/s aus 266°) und veränderte die berechnete Distanz/Zeit gegenüber demselben Aufruf ohne
Zeitpunkt; ein Zeitpunkt weit in der Vergangenheit fiel korrekt auf "kein Wind" zurück statt
zu scheitern; im echten Chrome bestätigt: Datumsfeld → Route berechnen → "Wind: 12 km/h aus
West" im Ergebnis sichtbar.

## 6.23 Phase 2 — Bugfix: Fallback-Auswahl ignorierte Dauer-Abweichung (durchgeführt)

Live vom Nutzer gemeldet (2026-08-31): ein 120-min-GA1-Plan (FTP 148 W, 38 kg - extremes, aber
gültiges Profil) lieferte nur 24,4 km/58 min. Ursache gefunden und reproduziert: bei eng
gesetzten Grenzwerten (`maxUnpavedSegmentMeters=100`, `maxTotalUnpavedMeters=1000`,
`maxDisruptiveJunctions=30`) erfüllte KEINE der 100 probierten Streckenvarianten alle
Grenzwerte - die "beste gefundene Variante" (`BuildRouteAsync`s Badness-Fallback, siehe 6.9/6.12)
wählt dann nur nach Untergrund/Kreuzungen, OHNE je zu prüfen, ob die Distanz überhaupt zum
Trainingsplan passt. Ein round_trip-Seed, der zufällig eine deutlich KÜRZERE Schleife liefert
(reale GraphHopper-round_trip-Ungenauigkeit), hat automatisch weniger absolute unbefestigte
Meter/Kreuzungen einer kürzeren Route und "gewinnt" die Bewertung, obwohl er den Trainingsplan
komplett verfehlt.

**Fix:** Badness-Formel um einen Dauer-Abweichungs-Term erweitert
(`DurationMismatchBadnessWeightMetersPerSecond = 10.0` - 10 Badness-Meter pro Sekunde
Abweichung von der Plandauer, dominiert damit schon bei wenigen Minuten Abweichung jede
realistische Untergrund-/Kreuzungs-Differenz). Zusätzlich `CheckApproachBudget` (4.4) auf BEIDE
Richtungen erweitert - bisher wurde nur "Route zu LANG" gewarnt, nie "Route zu KURZ", obwohl
genau das hier passiert ist. Dieselbe `MaxApproachMinutes`-Toleranz gilt jetzt symmetrisch.

Getestet: 2 neue Unit-Tests (Fallback bevorzugt jetzt die dauer-korrekte Variante trotz etwas
mehr unbefestigtem Untergrund; Warnung erscheint, wenn selbst die beste Variante die Dauer klar
verfehlt), `FakeGraphHopperClient` um `ActualDistanceMetersBySeed` erweitert (simuliert reale
round_trip-Ungenauigkeit - zurückgegebene Distanz muss nicht der angeforderten entsprechen),
volle Testsuite (74/74) grün. Live mit den exakten gemeldeten Parametern erneut verifiziert:
40,3 km/97 min statt vorher 24,4 km/58 min - deutlich näher am 120-min-Ziel.

**Update (2026-08-31, ebenfalls behoben):** die zunächst als separat/größerer Aufwand
eingestufte Ungenauigkeit (siehe unten) wurde direkt im Anschluss ebenfalls behoben - siehe
6.24.

**Ursprünglich verbleibende, separate Ungenauigkeit (jetzt behoben, siehe 6.24):** die
angezeigte "Geschätzte Zeit" stammte direkt aus GraphHoppers eigener `time`-Antwort (berechnet
mit dem festen `limit_to: 25`-Profil-Speed, siehe 6.17), NICHT aus einer Neuberechnung über
`PowerSpeedModel`/das tatsächliche Fahrerprofil. Bei Rennrad-typischen Profilen (FTP~250W/75kg)
liegt GA1-Tempo (~24 km/h) nah genug an den flachen 25 km/h, dass der Unterschied kaum auffiel -
bei einem sehr leichten/leistungsschwachen Profil wie im gemeldeten Fall (~21 km/h GA1-Tempo)
zeigte sich die Lücke deutlich (97 statt der eigentlich angepeilten ~120 min).

## 6.24 Phase 2 — Bugfix: "Geschätzte Zeit" nutzt jetzt das eigene physikbasierte Modell (durchgeführt)

Direkter Nachtrag zu 6.23, noch am selben Tag umgesetzt. `EstimatedTotalTime` kam bisher direkt
aus `finalRoute.Time` (GraphHoppers eigene, fest auf 25 km/h gesetzte Schätzung, siehe 6.17) -
unabhängig vom tatsächlichen Fahrerprofil.

**Fix:** Neue Methode `EstimateTotalTime` rekonstruiert die Gesamtzeit über `PowerSpeedModel`:
Effort-Schritte MIT gefundenem Korridor nutzen dessen TATSÄCHLICHE Länge (kann durch die
Fallback-Eskalation von der ursprünglichen Schätzung abweichen). Alle übrigen Schritte (ruhige
Blöcke UND Effort-Schritte ohne gefundenen Korridor) teilen sich die tatsächlich VERBLEIBENDE
Distanz (`finalRoute.DistanceMeters` abzüglich aller Korridor-Längen) proportional zu ihrem
ursprünglich geschätzten Anteil auf - das fängt GraphHoppers round_trip-Ungenauigkeit (die
tatsächliche Distanz weicht oft von der angeforderten ab, siehe 6.23) korrekt mit ein, statt
naiv unveränderte Schätzungen aufzusummieren. `RefineRoughLoopAsync` gibt dafür zusätzlich die
in der letzten Iteration verwendeten Steigungs-/Windwerte pro Schritt zurück, damit dieselben
Umgebungsbedingungen konsistent weiterverwendet werden. `CheckApproachBudget` (4.4/6.23)
vergleicht jetzt gegen diese neue Schätzung statt gegen `finalRoute.Time`.

Getestet: bestehender Test `ApproachBudgetExceeded_AddsWarning` angepasst (simuliert jetzt einen
zu langen KORRIDOR statt eines künstlich hohen `finalRoute.Time`-Werts, da Letzteres nicht mehr
gelesen wird), volle Testsuite (74/74) grün. Live mit den exakten gemeldeten Parametern erneut
verifiziert: **02:00:16 (120 min 16s) statt vorher 97 min** - praktisch exakt das 120-min-Ziel.
Die dabei gewählte Distanz (30 km statt der zuvor grob abgeschätzten ~42 km) erklärt sich durch
echten, in dieser Berechnung bereits berücksichtigten Gegenwind (siehe 6.22), den eine grobe
Handrechnung ohne Windmodell nicht erfasst hätte. Zusätzlich mit einem normalen Fahrerprofil
(250 W/75 kg, gemischter Ruhe-/Effort-Plan) gegengeprüft: Ergebnis bleibt im erwarteten
Anfahrt-Budget-Rahmen, mit korrekter Korridor-Lockerungs-Warnung.

## 6.25 Phase 2 — Login-Pflicht + gespeichertes Fahrerprofil (durchgeführt)

Vom Nutzer direkt beim vorherigen Bugfixing angestoßen: Login soll notwendig sein, um WattLoop
überhaupt nutzen zu können - und FTP/Gewicht/Sprint-Watt sollen ins Konto-Profil aufgenommen
werden (erster konkreter Nutzen eines eingeloggten Zustands, siehe Phase-4-Backlog
"Mehrbenutzerfähigkeit/Auth/Vereine").

**Backend:** Neue Entität `UserRiderProfile` (1:1 pro Nutzer, `UserId` als Primary Key,
`TrainingRoutePlanner.Data`), Migration erstellt und live gegen Neon angewendet. `GET /profile`
(404 wenn noch nichts gespeichert - kein Fehlerzustand) und `PUT /profile` (Upsert), beide
`.RequireAuthorization()`. ALLE app-funktionalen Endpunkte (`/route`, `/workout/build`,
`/junctions`) jetzt ebenfalls `.RequireAuthorization()` - Login ist damit nicht nur eine
Frontend-Gate-Kosmetik, sondern serverseitig durchgesetzt. `/health` bleibt bewusst offen
(Render-Health-Check).

**Frontend:** Kein Zugriff auf die App ohne Login mehr - `App.tsx` zeigt ohne gültiges Token
NUR noch ein zentriertes Login/Registrierungs-Formular (`.auth-gate`), nicht mehr die
Sidebar/Karte. Ein kurzer `authInitializing`-Zwischenzustand verhindert ein Aufblitzen des
Login-Formulars, waehrend ein gespeichertes Token noch gegen `/auth/me` geprüft wird. Nach
Login/Registrierung wird automatisch das gespeicherte Profil geladen und in die
FTP/Gewicht/Sprint-Watt-Felder übernommen (falls vorhanden); bei jeder erfolgreichen
Routenberechnung wird das Profil mit den aktuell eingegebenen Werten automatisch aktualisiert
(kein separater "Speichern"-Button - nicht kritisch/nicht blockierend, ein Fehlschlag verwirft
nicht die gerade berechnete Route).

Getestet: volle Backend-Testsuite (74/74) unverändert grün (betroffene Endpunkte sind reine
Minimal-API-Verdrahtung, nicht in den bestehenden RouteConstructionService-Unit-Tests
abgedeckt). Live gegen die echte Neon-DB verifiziert: `/junctions` ohne Token 401, mit Token
200; `/profile` GET vor erstem Speichern 404, PUT dann GET liefert die gespeicherten Werte
korrekt zurück. Im echten Chrome bestätigt: kein Zugriff auf die App ohne Login (nur das
Formular sichtbar), nach Registrierung volle App nutzbar, individuelle FTP/Gewicht-Werte (199/
64) übersteigen einen Reload unverändert (aus dem gespeicherten Profil geladen, nicht auf die
Standardwerte zurückgefallen), Abmelden führt zurück zum Login-Gate.

## 7. Offene Punkte

- **Windschatten/Gruppenfahrt** - vom Nutzer vorgeschlagen (2026-08-31), noch keine konkrete
  Umsetzungsidee. Naheliegender Ansatz: Windschatten in der Gruppe reduziert effektiv den
  Luftwiderstand (`RiderProfile.DragAreaCdA`), typischerweise auf ca. 70-90% des Solo-Werts je
  nach Position/Gruppengroesse - liesse sich als "fahre ich in der Gruppe?"-Schalter oder
  Gruppengroesse-Eingabe umsetzen, die `PowerSpeedModel`s CdA entsprechend herunterskaliert.
  Haengt eng mit der Windmodellierung (6.22) und der Zeitschaetzung (6.23) zusammen, da alle
  drei denselben physikbasierten Geschwindigkeits-Kern beruehren.
- Kalibrierung der genauen Score-Gewichte und Zonen-Schwellwerte (aktuell Platzhalter-Werte,
  brauchen echte Trainingsfahrten zur Kalibrierung — explizit Teil von Phase 3, nicht vorher
  lösbar)
- Spatial Index für die Korridorsuche, falls Regionsgröße/Anfragevolumen den linearen Scan zum
  Flaschenhals machen (Performance-Optimierung, aktuell kein akutes Problem)
- Garmin.FIT.Sdk 21.214.0 `wkt_step_name`-Dekodierfehler bei gemischten benannten/unbenannten
  Schritten (siehe 6.2) - liegt in der Drittanbieter-SDK, nicht selbst behebbar; betrifft nur
  Anzeige-Labels, nicht die eigentliche Routenplanung
