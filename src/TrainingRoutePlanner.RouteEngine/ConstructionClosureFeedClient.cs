using System.Text.Json;
using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.RouteEngine;

/// <summary>Ruft die aktuell aktiven Baustellen-Sperrungen ab - siehe CONCEPT.md Abschnitt 6.27.
/// Liefert eine leere Liste statt zu werfen, wenn beide Feed-Varianten nicht erreichbar sind -
/// wie IWindForecastClient ist das ein rein additiver Kartenlayer/Routing-Zusatz, ein Ausfall
/// darf die Routenberechnung selbst nie verhindern.</summary>
public interface IConstructionClosureFeedClient
{
    Task<IReadOnlyList<ConstructionClosure>> FetchActiveClosuresAsync(DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>Thin wrapper um den kostenlosen, schluessellosen GeoJSON-Feed der Berliner
/// Verkehrsinformationszentrale (siehe CONCEPT.md Abschnitt 6.27 Recherche). Versucht zuerst den
/// reichhaltigeren viz.json-Feed (echtes severity-Feld, teils LineString-Geometrie) - nur wenn
/// der nicht erreichbar ist, den tic.json-Fallback (siehe ConstructionClosureFeedParser fuer
/// dessen bekannte Einschraenkungen). Die Datensatz-Seite selbst erwaehnt zwei parallele
/// Ressourcen wegen technischer Migration, ein Ausfall EINER der beiden ist also ein realistisch
/// erwartbarer Zustand, kein reiner Vorsichts-Fallback.</summary>
public sealed class VizBerlinConstructionClosureClient(HttpClient http) : IConstructionClosureFeedClient
{
    private const string PrimaryPath = "/daten/baustellen_sperrungen_viz.json";
    private const string FallbackPath = "/tic3/baustellen_sperrungen_tic.json";

    public async Task<IReadOnlyList<ConstructionClosure>> FetchActiveClosuresAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        try
        {
            var json = await http.GetStringAsync(PrimaryPath, ct);
            return ConstructionClosureFeedParser.ParseVizFeed(json, now);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            try
            {
                var json = await http.GetStringAsync(FallbackPath, ct);
                return ConstructionClosureFeedParser.ParseTicFallbackFeed(json, now);
            }
            catch (Exception ex2) when (ex2 is HttpRequestException or JsonException or TaskCanceledException)
            {
                return []; // Beide Feed-Varianten nicht erreichbar - Baustellen-Layer bleibt leer/unveraendert.
            }
        }
    }
}
