namespace TrainingRoutePlanner.Domain;

/// <summary>Abstraktion ueber die vorberechneten Korridore einer Region (siehe CONCEPT.md
/// Abschnitt 4.1), implementiert von TrainingRoutePlanner.OsmCorridors. Liegt in Domain,
/// damit sowohl OsmCorridors als auch RouteEngine ohne Zirkelbezug dagegen arbeiten koennen.</summary>
public interface ICorridorIndex
{
    Corridor? TryFindCorridor(GeoPoint near, double minLengthMeters, double maxDisruptionScore, double searchRadiusMeters);

    /// <summary>Zaehlt die Anzahl UNTERSCHIEDLICHER Ampel-/Stopp-Knoten (harte
    /// Unterbrechungen, siehe CONCEPT.md 3.4), die innerhalb von <paramref name="proximityMeters"/>
    /// irgendeines Punktes der gegebenen Routengeometrie liegen - fuer die Bewertung der
    /// GESAMTEN Route (nicht nur einzelner Korridor-Abschnitte), siehe RouteRequest.
    /// MaxDisruptiveJunctions.</summary>
    int CountDisruptiveJunctionsNear(IReadOnlyList<GeoPoint> routeGeometry, double proximityMeters);
}
