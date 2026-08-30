namespace TrainingRoutePlanner.Domain;

/// <summary>Ein vom Nutzer auf der Karte markierter Bereich, der bei der Routenberechnung
/// komplett gemieden werden soll (siehe CONCEPT.md Abschnitt 6.18) - z.B. eine gesperrte
/// Straße oder ein Abschnitt, den der Nutzer aus persönlichen Gründen nicht fahren möchte.
/// Wird als Kreis um <see cref="Center"/> an GraphHopper durchgereicht (per-Request
/// custom_model mit "areas", da der Klassiker "block_area" von GraphHopper entfernt wurde).</summary>
public sealed record BlockedArea(GeoPoint Center, double RadiusMeters);
