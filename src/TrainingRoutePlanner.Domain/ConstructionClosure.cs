namespace TrainingRoutePlanner.Domain;

/// <summary>Nur die beiden Sperrgrade, die fuer die Routenberechnung ueberhaupt relevant sind
/// (siehe CONCEPT.md Abschnitt 6.27) - "keine Sperrung" (reine Fahrstreifenverengung) wird schon
/// beim Parsen des Feeds verworfen und taucht hier nie auf. Directional (deutsch:
/// "Fahrtrichtungssperrung") wird bewusst GENAUSO behandelt wie Full ("Vollsperrung"): GraphHopper-
/// Wege sind fuer Fahrraeder i.d.R. beidseitig befahrbar, die gesperrte Fahrtrichtung liesse sich
/// nicht trivial mit der eigenen Fahrtrichtung abgleichen - lieber einmal unnoetig umfahren als
/// durch eine gesperrte Richtung geroutet werden.</summary>
public enum ClosureSeverity
{
    Full,
    Directional,
}

/// <summary>Eine aktuell gueltige Baustellen-Sperrung aus dem VIZ-Berlin-Feed (siehe CONCEPT.md
/// Abschnitt 6.27) - fachlich das automatisch erkannte Gegenstueck zu <see cref="BlockedArea"/>
/// (dort vom Nutzer manuell auf der Karte markiert). <see cref="Geometry"/> ist entweder ein
/// einzelner Punkt ODER eine Punktfolge (LineString) entlang der betroffenen Strasse - beide
/// Faelle werden beim Kodieren in GraphHoppers custom_model unterschiedlich behandelt (siehe
/// GraphHopperClient.BuildClosurePolygon). <see cref="Id"/> ist die stabile ID aus dem Feed (Feld
/// "id"), noetig, damit der Nutzer eine einzelne erkannte Baustelle gezielt fuer seine Route
/// ignorieren kann (siehe RouteRequest.ConstructionClosures und Program.cs /route -
/// "ignoredConstructionClosureIds"), da die Daten editoriell kuratiert und nicht 100% vollstaendig/
/// fehlerfrei sind.</summary>
public sealed record ConstructionClosure(
    string Id,
    string Street,
    IReadOnlyList<GeoPoint> Geometry,
    ClosureSeverity Severity,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo);
