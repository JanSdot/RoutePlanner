using System.Globalization;
using System.Text.Json;
using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.RouteEngine;

/// <summary>Parst den GeoJSON-Feed der Berliner Verkehrsinformationszentrale (VIZ), siehe
/// CONCEPT.md Abschnitt 6.27 fuer die vorangegangene Recherche. Es gibt zwei parallele
/// Ressourcen (laut Datensatz-Seite wegen technischer Migration): baustellen_sperrungen_viz.json
/// (primaer - ISO-8601-Datumsformat, echtes "severity"-Feld, teils LineString-Geometrie entlang
/// der Strasse) und baustellen_sperrungen_tic.json (Fallback, falls viz.json nicht erreichbar
/// ist - deutsches Datumsformat "31.12.2026 23:59", "severity" ist dort in der Stichprobe
/// DURCHGEHEND null). Reine JsonDocument-Navigation statt typisierter DTOs, weil die
/// Geometrie-Form pro Feature variiert (Point/LineString/GeometryCollection) und ein Property
/// zwischen den beiden Feed-Varianten fehlen kann.</summary>
public static class ConstructionClosureFeedParser
{
    // Reine Fahrstreifenverengung ohne Voll-/Richtungssperrung - kein routing-relevantes
    // Hindernis (siehe CONCEPT.md 6.27 Recherche: von 226 Eintraegen im viz.json-Feed waren nur
    // 73 tatsaechliche Voll-/Richtungssperrungen), daher schon beim Parsen verworfen statt erst
    // spaeter gefiltert.
    private const string FullClosureSeverityValue = "Vollsperrung";
    private const string DirectionalClosureSeverityValue = "Fahrtrichtungssperrung";

    /// <summary>Primaerer Feed (baustellen_sperrungen_viz.json) - hat ein echtes severity-Feld,
    /// ISO-8601-Datumsangaben.</summary>
    public static IReadOnlyList<ConstructionClosure> ParseVizFeed(string json, DateTimeOffset now)
        => ParseFeatures(json, now, isTicFallback: false);

    /// <summary>Fallback-Feed (baustellen_sperrungen_tic.json), nur relevant wenn viz.json nicht
    /// erreichbar ist. Liefert in der Praxis KEIN severity-Feld (durchgehend null in der
    /// Recherche-Stichprobe) - ohne diese Information laesst sich Voll-/Richtungssperrung nicht
    /// von reiner Fahrstreifenverengung unterscheiden. Konservativ (siehe ClosureSeverity-
    /// Dokumentation: "lieber einmal unnoetig umfahren") wird daher JEDE Baustelle dieses
    /// Fallback-Feeds als Directional behandelt - eine bewusste Ungenauigkeit, die nur in dem
    /// seltenen Fall zum Tragen kommt, dass der eigentliche (reichhaltigere) Feed ausfaellt.
    /// Datumsangaben liegen hier im deutschen Format vor ("31.12.2026 23:59") statt ISO 8601.</summary>
    public static IReadOnlyList<ConstructionClosure> ParseTicFallbackFeed(string json, DateTimeOffset now)
        => ParseFeatures(json, now, isTicFallback: true);

    private static IReadOnlyList<ConstructionClosure> ParseFeatures(string json, DateTimeOffset now, bool isTicFallback)
    {
        var result = new List<ConstructionClosure>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("features", out var features))
            return result;

        foreach (var feature in features.EnumerateArray())
        {
            var closure = ParseFeature(feature, now, isTicFallback);
            if (closure is not null)
                result.Add(closure);
        }
        return result;
    }

    private static ConstructionClosure? ParseFeature(JsonElement feature, DateTimeOffset now, bool isTicFallback)
    {
        if (!feature.TryGetProperty("properties", out var props) || !feature.TryGetProperty("geometry", out var geometry))
            return null;

        ClosureSeverity? severity = isTicFallback
            ? ClosureSeverity.Directional
            : MapSeverity(props.TryGetProperty("severity", out var sevEl) ? sevEl.GetString() : null);
        if (severity is null)
            return null; // "keine Sperrung" oder unbekannter/fehlender Wert - nicht routing-relevant.

        var (validFrom, validTo) = ParseValidity(props, isTicFallback);
        if (!IsActive(validFrom, validTo, now))
            return null;

        var geometryPoints = ExtractGeometry(geometry);
        if (geometryPoints.Count == 0)
            return null;

        var id = props.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            return null; // Ohne stabile ID koennte der Nutzer diese Baustelle nicht gezielt ignorieren.

        var street = props.TryGetProperty("street", out var streetEl) ? streetEl.GetString() ?? "" : "";

        return new ConstructionClosure(id, street, geometryPoints, severity.Value, validFrom, validTo);
    }

    private static ClosureSeverity? MapSeverity(string? raw) => raw switch
    {
        FullClosureSeverityValue => ClosureSeverity.Full,
        DirectionalClosureSeverityValue => ClosureSeverity.Directional,
        _ => null, // deckt "keine Sperrung" UND fehlende/unbekannte Werte ab.
    };

    private static (DateTimeOffset? From, DateTimeOffset? To) ParseValidity(JsonElement props, bool isTicFallback)
    {
        if (!props.TryGetProperty("validity", out var validity))
            return (null, null);
        var fromRaw = validity.TryGetProperty("from", out var f) ? f.GetString() : null;
        var toRaw = validity.TryGetProperty("to", out var t) ? t.GetString() : null;
        return (ParseDate(fromRaw, isTicFallback), ParseDate(toRaw, isTicFallback));
    }

    // Fehlendes/leeres "from" bedeutet "schon immer gueltig", fehlendes "to" bedeutet "bis auf
    // weiteres gueltig" (in der viz.json-Stichprobe kommt ein fehlendes "to" tatsaechlich vor) -
    // beides als null durchgereicht, IsActive behandelt null an beiden Enden als "keine
    // Einschraenkung in diese Richtung".
    private static DateTimeOffset? ParseDate(string? raw, bool isTicFallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (isTicFallback)
        {
            // Deutsches Format "31.12.2026 23:59" statt ISO 8601 - siehe Klassen-Dokumentation.
            return DateTimeOffset.TryParseExact(
                raw, "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var ticDate)
                ? ticDate
                : null;
        }
        // viz.json: ISO 8601 ohne Zeitzonen-Suffix ("2025-07-23T07:00"). AssumeUniversal statt
        // die Serverzeitzone raten zu lassen - fuer ein grobes Tages-Gueltigkeitsfenster (kein
        // minutengenauer Vergleich) reicht das voellig aus.
        return DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
    }

    private static bool IsActive(DateTimeOffset? from, DateTimeOffset? to, DateTimeOffset now)
        => (from is null || from <= now) && (to is null || to >= now);

    private static List<GeoPoint> ExtractGeometry(JsonElement geometry)
    {
        var type = geometry.TryGetProperty("type", out var t) ? t.GetString() : null;
        return type switch
        {
            "Point" => ExtractPointCoordinates(geometry) is { } p ? [p] : [],
            "LineString" => ExtractCoordinatesArray(geometry),
            "GeometryCollection" => ExtractFromCollection(geometry),
            _ => [],
        };
    }

    // Bevorzugt die LineString-Geometrie (praeziser entlang der Strasse, siehe CONCEPT.md 6.27
    // Recherche) - faellt auf den Point zurueck, falls die Collection keinen LineString enthaelt.
    private static List<GeoPoint> ExtractFromCollection(JsonElement geometry)
    {
        if (!geometry.TryGetProperty("geometries", out var geometries))
            return [];

        JsonElement? pointGeom = null;
        foreach (var g in geometries.EnumerateArray())
        {
            var t = g.TryGetProperty("type", out var te) ? te.GetString() : null;
            if (t == "LineString")
                return ExtractCoordinatesArray(g);
            if (t == "Point")
                pointGeom = g;
        }
        return pointGeom is { } pg && ExtractPointCoordinates(pg) is { } p ? [p] : [];
    }

    // GeoJSON-Konvention ist [lon, lat] - entgegengesetzt zu unserem eigenen GeoPoint(Lat, Lon),
    // wie schon in GraphHopperClient.ToLonLat.
    private static GeoPoint? ExtractPointCoordinates(JsonElement pointGeometry)
    {
        if (!pointGeometry.TryGetProperty("coordinates", out var coords) || coords.GetArrayLength() < 2)
            return null;
        return new GeoPoint(coords[1].GetDouble(), coords[0].GetDouble());
    }

    private static List<GeoPoint> ExtractCoordinatesArray(JsonElement lineGeometry)
    {
        var result = new List<GeoPoint>();
        if (!lineGeometry.TryGetProperty("coordinates", out var coords))
            return result;
        foreach (var c in coords.EnumerateArray())
        {
            if (c.GetArrayLength() < 2) continue;
            result.Add(new GeoPoint(c[1].GetDouble(), c[0].GetDouble()));
        }
        return result;
    }
}
