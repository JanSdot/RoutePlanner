using TrainingRoutePlanner.RouteEngine;

namespace TrainingRoutePlanner.Api;

/// <summary>Aktualisiert den Baustellen-Sperrungen-Cache stuendlich im Hintergrund, statt den
/// VIZ-Berlin-Feed pro Nutzer-Request abzurufen - siehe CONCEPT.md Abschnitt 6.27 ("guter
/// API-Buerger, spart Latenz", passend zur eigenen stuendlichen Aktualisierungsfrequenz der
/// Quelle). Ein fehlgeschlagener Abruf (z.B. beide Feed-Varianten kurzzeitig nicht erreichbar)
/// laesst einfach den ALTEN Cache-Stand stehen statt die App zum Absturz zu bringen - IMMER noch
/// besser als ein leerer/veralteter Layer fuer eine Stunde.</summary>
public sealed class ConstructionClosureRefreshService(
    IConstructionClosureFeedClient feedClient,
    ConstructionClosureCache cache,
    ILogger<ConstructionClosureRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var closures = await feedClient.FetchActiveClosuresAsync(DateTimeOffset.UtcNow, stoppingToken);
                cache.SetActive(closures);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Baustellen-Feed-Aktualisierung fehlgeschlagen - vorheriger Cache-Stand bleibt aktiv.");
            }

            try
            {
                await Task.Delay(RefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // App faehrt herunter - kein Fehler, einfach Schleife (und damit den Service) beenden.
            }
        }
    }
}
