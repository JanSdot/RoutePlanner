namespace TrainingRoutePlanner.Domain;

/// <summary>Klassifiziert OSM/GraphHopper <c>surface</c>-Werte (siehe SurfaceSegment) als
/// "unbefestigt" fuer Anzeige (frontend MapView.tsx) und Vermeidung (RouteConstructionService).
/// Bewusst eine Denyliste statt einer Erlaubnisliste: viele asphaltierte Strassen tragen in OSM
/// gar kein surface-Tag (GraphHopper liefert dann "missing"), waehrend surface=unpaved/gravel/...
/// fast immer explizit gesetzt wird, gerade WEIL es die Ausnahme ist - eine Erlaubnisliste wuerde
/// die meisten echten Asphaltstrecken faelschlich markieren. Muss mit UNPAVED_SURFACES in
/// frontend/src/components/MapView.tsx abgeglichen bleiben.</summary>
public static class SurfaceClassifier
{
    private static readonly HashSet<string> UnpavedSurfaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "unpaved", "gravel", "fine_gravel", "dirt", "ground", "sand", "mud", "grass", "grass_paver",
        "pebblestone", "cobblestone", "sett", "unhewn_cobblestone", "compacted", "woodchips", "rock",
    };

    public static bool IsUnpaved(string surface) => UnpavedSurfaces.Contains(surface);
}
