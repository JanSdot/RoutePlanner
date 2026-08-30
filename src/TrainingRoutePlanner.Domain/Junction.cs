namespace TrainingRoutePlanner.Domain;

/// <summary>Eine Ampel oder ein Stoppschild, fuer den optionalen Karten-Layer (CONCEPT.md
/// Abschnitt 6.21). Rein informativ/Anzeige - unabhaengig von RouteRequest.
/// MaxDisruptiveJunctions (das zaehlt nur Treffer NAHE einer gegebenen Route).</summary>
public sealed record Junction(GeoPoint Point, HardNodeType Type);
