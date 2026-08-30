using System.Runtime.CompilerServices;

// The core algorithm (graph, extraction, scoring, sliding window) is deliberately internal -
// CorridorIndex + Domain.Corridor are the only public surface other modules should depend on.
// Tests need direct access to build synthetic graphs without a real .osm.pbf file, see CONCEPT.md
// testing requirements.
[assembly: InternalsVisibleTo("TrainingRoutePlanner.Tests")]
