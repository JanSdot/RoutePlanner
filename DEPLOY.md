# Deployment auf Render.com

Dieses Repo enthält ein [`render.yaml`](render.yaml) ("Blueprint"), das alle drei Services auf
einmal einrichtet.

## Schritte

1. Repo zu GitHub/GitLab pushen (Render braucht eine Git-Verbindung, kein lokales Deploy).
2. Auf [render.com](https://render.com) → "New" → "Blueprint" → Repo auswählen. Render liest
   `render.yaml` automatisch und schlägt drei Services vor:
   - `trainingrouteplanner-graphhopper` (Docker, Routing-Engine)
   - `trainingrouteplanner-api` (Docker, C#-Backend)
   - `trainingrouteplanner-frontend` (Static Site, React-Frontend)
3. **Wichtig:** Für `trainingrouteplanner-graphhopper` den Plan auf mindestens **"Standard"
   (2 GB RAM)** setzen, nicht den kostenlosen/kleinsten Plan — die 60-km-Region um Berlin passt
   nicht in 512 MB (siehe Kommentar in `render.yaml`). Für `trainingrouteplanner-api` ebenfalls,
   da `CorridorIndex.Load` dieselbe Datenmenge im Speicher hält.
4. Deploy starten. Beide Docker-Builds laden bei jedem Build den Berlin+Brandenburg-OSM-Extrakt
   frisch von Geofabrik (~380 MB) und schneiden ihn per `osmium` zu — das dauert mehrere Minuten
   pro Build, hält das Repo aber frei von großen Binärdateien.
5. Nach dem ersten erfolgreichen Deploy: die tatsächlich zugewiesenen `*.onrender.com`-URLs im
   Render-Dashboard prüfen. Falls einer der Servicenamen (z. B. `trainingrouteplanner-api`)
   bereits von jemand anderem belegt war, bekommt der Service eine abweichende URL (mit
   Zufalls-Suffix) — dann müssen die beiden fest eingetragenen Werte in `render.yaml` angepasst
   werden:
   - `trainingrouteplanner-api`: Umgebungsvariable `Cors__AllowedOrigin` → tatsächliche
     Frontend-URL
   - `trainingrouteplanner-frontend`: Umgebungsvariable `VITE_API_BASE_URL` → tatsächliche
     API-URL (danach den Frontend-Service neu deployen, da Vite die URL beim Build einbrennt)

## Bekannte Einschränkungen

- **Erste Anfrage nach jedem Deploy/Neustart ist langsam** (~15–20 s): `CorridorIndex.Load`
  parst die OSM-Datei und baut den Korridor-Index erst beim ersten `/route`-Aufruf (lazy
  Singleton), nicht beim Start. Das betrifft nur die erste Anfrage nach einem (Neu-)Start.
- **Nur die Region um Berlin/Brandenburg** (60 km um Sportforum Berlin) ist nutzbar — siehe
  CONCEPT.md Abschnitt 6.1. Für andere Regionen müssten die BBox-Koordinaten in beiden
  Dockerfiles sowie `graphhopper-config.yml` angepasst werden.
- **Kein Auth/Multi-User** — die App ist für alle erreichbar, die die URL kennen (siehe
  CONCEPT.md Phase 2/Phase 4 zu Mehrbenutzerfähigkeit als spätere Erweiterung).
- GraphHopper lädt SRTM-Höhendaten beim ersten Bedarf dynamisch nach (braucht ausgehenden
  Internetzugriff, den Render-Container standardmäßig haben).

## Lokal testen, bevor du deployst

Die Dockerfiles lassen sich auch lokal bauen/testen (dauert wegen des OSM-Downloads einige
Minuten):

```bash
docker build -f deploy/graphhopper.Dockerfile -t trp-graphhopper .
docker build -f deploy/api.Dockerfile -t trp-api .
docker run -p 8989:8989 trp-graphhopper
docker run -p 8080:8080 -e PORT=8080 -e GraphHopper__Host=host.docker.internal:8989 trp-api
```
