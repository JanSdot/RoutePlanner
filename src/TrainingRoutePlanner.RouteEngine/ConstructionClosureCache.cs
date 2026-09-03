using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.RouteEngine;

/// <summary>Haelt die zuletzt vom VIZ-Berlin-Feed abgerufenen, aktuell aktiven Baustellen-
/// Sperrungen im Speicher - siehe CONCEPT.md Abschnitt 6.27 ("Feed ist ~0.5-1 MB, serverseitig
/// einmal pro Stunde cachen statt pro Nutzer-Request abzurufen"). Volatile-Referenz statt Lock:
/// genau ein Hintergrund-Task (ConstructionClosureRefreshService) schreibt, beliebig viele
/// Requests lesen parallel - ein komplett ausgetauschter, unveraenderlicher Snapshot ist dafuer
/// ausreichend, ohne dass Leser je eine teilweise geschriebene Liste sehen koennten.</summary>
public interface IConstructionClosureCache
{
    IReadOnlyList<ConstructionClosure> GetActive();
}

public sealed class ConstructionClosureCache : IConstructionClosureCache
{
    private IReadOnlyList<ConstructionClosure> _active = [];

    public IReadOnlyList<ConstructionClosure> GetActive() => Volatile.Read(ref _active);

    public void SetActive(IReadOnlyList<ConstructionClosure> closures) => Volatile.Write(ref _active, closures);
}
