namespace TrainingRoutePlanner.Domain;

/// <summary>Art einer "harten" Kreuzung/Unterbrechung (siehe CONCEPT.md Abschnitt 3.4,
/// RoadGraph.HardNodes) - fuer den optionalen Ampeln/Stoppschilder-Kartenlayer, siehe
/// CONCEPT.md Abschnitt 6.21.</summary>
public enum HardNodeType
{
    TrafficSignal,
    Stop,
}
