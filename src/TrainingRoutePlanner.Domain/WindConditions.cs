namespace TrainingRoutePlanner.Domain;

/// <summary>Die fuer die Zeitschaetzung tatsaechlich verwendeten Windbedingungen (siehe
/// CONCEPT.md Phase-4-Backlog "Windmodellierung") - EIN Wert fuer die gesamte Fahrt (kein
/// Nachladen pro Segment), fuer Transparenz im Ergebnis mitgeliefert (z.B. "18 km/h aus West").
/// WindFromDirectionDegrees folgt der meteorologischen Konvention: die Richtung, AUS der der
/// Wind weht (0=Nord, 90=Ost, ...), nicht wohin er weht.</summary>
public sealed record WindConditions(double WindSpeedMps, double WindFromDirectionDegrees);
