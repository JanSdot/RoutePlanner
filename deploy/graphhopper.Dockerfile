# Baut den GraphHopper-Routing-Service inkl. eines auf 60km um das Sportforum Berlin
# zugeschnittenen OSM-Datenextrakts (siehe CONCEPT.md Abschnitt 6.1 fuer die Herleitung dieser
# Region/BBox). Build-Kontext muss der Repo-Root sein (siehe render.yaml: dockerContext: .).

# --- Stage 1: OSM-Datenextrakt bauen (Geofabrik-Download + osmium-Zuschnitt, wie in Phase 0) ---
FROM debian:bookworm-slim AS osmdata
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl osmium-tool ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /data
RUN curl -fL -o berlin.osm.pbf https://download.geofabrik.de/europe/germany/berlin-latest.osm.pbf \
    && curl -fL -o brandenburg.osm.pbf https://download.geofabrik.de/europe/germany/brandenburg-latest.osm.pbf \
    && osmium merge berlin.osm.pbf brandenburg.osm.pbf -o merged.osm.pbf --overwrite \
    && osmium extract -b 12.5914,52.0021,14.3614,53.0831 merged.osm.pbf -o sportforum-60km.osm.pbf --overwrite \
    && rm berlin.osm.pbf brandenburg.osm.pbf merged.osm.pbf

# --- Stage 2: HealthProxy kompilieren (siehe CONCEPT.md 6.26) - eigene Stage, weil das
# schlanke JRE-Laufzeit-Image (Stage 3) kein javac mitbringt. ---
FROM eclipse-temurin:21-jdk-jammy AS proxybuild
WORKDIR /build
COPY deploy/graphhopper/HealthProxy.java .
RUN javac HealthProxy.java

# --- Stage 3: Laufzeit-Image ---
FROM eclipse-temurin:21-jre-jammy
WORKDIR /app

# ADD kann eine URL direkt herunterladen, ohne dass curl im schlanken JRE-Image installiert
# sein muss (RUN curl wuerde hier fehlschlagen, da dieses Basisimage kein curl mitbringt).
ADD https://github.com/graphhopper/graphhopper/releases/download/11.0/graphhopper-web-11.0.jar \
    ./graphhopper-web.jar

COPY --from=osmdata /data/sportforum-60km.osm.pbf ./data/sportforum-60km.osm.pbf
COPY --from=proxybuild /build/HealthProxy.class ./HealthProxy.class
COPY deploy/graphhopper-config.yml ./config.yml

# 8989 gehoert jetzt dem HealthProxy (CONCEPT.md 6.26), NICHT mehr direkt GraphHopper - der
# importiert weiterhin im Hintergrund auf Port 18989 (siehe config.yml), waehrend der Proxy
# sofort auf 8989 antwortet und Renders Health-Check damit lange vor Ende des Imports besteht.
EXPOSE 8989
# -Xmx bewusst als Umgebungsvariable statt fest kodiert - siehe render.yaml Kommentar zur
# Instanzgroesse (dieses Kartenmaterial braucht mehr als die 512MB der kleinsten Render-Stufe).
ENV JAVA_MAX_HEAP=2g
ENV PROXY_PORT=8989
ENV GRAPHHOPPER_INTERNAL_PORT=18989
# GraphHopper im Hintergrund starten (Import + Server auf dem internen Port), HealthProxy per
# exec als Hauptprozess (PID 1-Ersatz) im Vordergrund - siehe HealthProxy.java fuer das Verhalten,
# wenn GraphHopper abstuerzt/nie bereit wird.
CMD ["sh", "-c", "java -Xmx${JAVA_MAX_HEAP} -jar graphhopper-web.jar server config.yml & exec java HealthProxy"]
