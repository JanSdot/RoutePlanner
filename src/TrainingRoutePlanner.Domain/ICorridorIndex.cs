namespace TrainingRoutePlanner.Domain;

/// <summary>Abstraktion ueber die vorberechneten Korridore einer Region (siehe CONCEPT.md
/// Abschnitt 4.1), implementiert von TrainingRoutePlanner.OsmCorridors. Liegt in Domain,
/// damit sowohl OsmCorridors als auch RouteEngine ohne Zirkelbezug dagegen arbeiten koennen.</summary>
public interface ICorridorIndex
{
    Corridor? TryFindCorridor(GeoPoint near, double minLengthMeters, double maxDisruptionScore, double searchRadiusMeters);
}
