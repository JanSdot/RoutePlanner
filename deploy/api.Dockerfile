# Baut den C#-Backend-Service inkl. desselben OSM-Datenextrakts wie graphhopper.Dockerfile
# (CorridorIndex.Load braucht dieselbe Region). Build-Kontext muss der Repo-Root sein (siehe
# render.yaml: dockerContext: .).

# --- Stage 1: OSM-Datenextrakt bauen (identisch zu graphhopper.Dockerfile) ---
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

# --- Stage 2: .NET-Build ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ .
RUN dotnet restore TrainingRoutePlanner.slnx
RUN dotnet publish TrainingRoutePlanner.Api/TrainingRoutePlanner.Api.csproj -c Release -o /app/publish --no-restore

# --- Stage 3: Laufzeit-Image ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=osmdata /data/sportforum-60km.osm.pbf ./data/sportforum-60km.osm.pbf

ENV OsmCorridors__PbfPath=/app/data/sportforum-60km.osm.pbf
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "TrainingRoutePlanner.Api.dll"]
