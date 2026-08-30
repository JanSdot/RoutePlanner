namespace TrainingRoutePlanner.Domain;

public sealed class RouteRequest
{
    public required GeoPoint StartPoint { get; init; }
    public required TrainingPlan Plan { get; init; }
    public required RiderProfile Rider { get; init; }

    /// <summary>Gesamtbudget fuer Hin- und Rueckweg zusammen, siehe CONCEPT.md 4.4.</summary>
    public double MaxApproachMinutes { get; init; } = 30;

    public SegmentReusePreference SegmentReuse { get; init; } = SegmentReusePreference.PreferReuse;

    /// <summary>Wenn false: kein exaktes Wiederverwenden desselben Korridors fuer
    /// Wiederholungen desselben Trainingsschritts (das erzwingt sonst haeufig eine Kehrtwende,
    /// weil GraphHopper vom Korridorende zurueck zum -anfang denselben Weg zurueckroutet).
    /// Zusaetzlich wird die finale Route auf abrupte Richtungswechsel geprueft und ggf. mit
    /// einer Warnung gekennzeichnet - kann in duennen Strassennetzen nicht immer vollstaendig
    /// vermieden werden.</summary>
    public bool AllowUTurns { get; init; } = true;

    /// <summary>Maximal erlaubte Laenge EINES zusammenhaengenden Abschnitts mit unbefestigtem
    /// Untergrund (siehe SurfaceClassifier.IsUnpaved), null = keine Begrenzung. Der Algorithmus
    /// probiert bei Ueberschreitung mehrere Routen-Varianten durch (RouteConstructionService),
    /// kann eine Einhaltung aber nicht garantieren - siehe RouteResult.Warnings, wenn selbst der
    /// beste gefundene Versuch die Grenze noch reisst.</summary>
    public double? MaxUnpavedSegmentMeters { get; init; }

    /// <summary>Maximal erlaubte Gesamtlaenge unbefestigter Abschnitte ueber die gesamte Route
    /// summiert, null = keine Begrenzung. Siehe MaxUnpavedSegmentMeters.</summary>
    public double? MaxTotalUnpavedMeters { get; init; }

    /// <summary>Maximal erlaubte Anzahl unterschiedlicher Ampel-/Stopp-Kreuzungen (siehe
    /// CONCEPT.md 3.4 "harte Unterbrechungen") entlang der GESAMTEN Route, null = keine
    /// Begrenzung. Nutzt denselben Retry-Mechanismus wie MaxUnpavedSegmentMeters/
    /// MaxTotalUnpavedMeters (RouteConstructionService) - selbe Garantie-Einschraenkung.</summary>
    public int? MaxDisruptiveJunctions { get; init; }

    /// <summary>Wie viele Routen-Varianten (unterschiedliche round_trip-Seeds) maximal
    /// durchprobiert werden, wenn MaxUnpavedSegmentMeters/MaxTotalUnpavedMeters/
    /// MaxDisruptiveJunctions gesetzt sind - null = Standardwert von RouteConstructionService
    /// (siehe dort). Wirkungslos, wenn keines der drei Limits gesetzt ist (dann laeuft ohnehin
    /// immer nur ein einziger Versuch). Das feste Zeitbudget in RouteConstructionService bleibt
    /// unabhaengig davon als Sicherheitsnetz bestehen, siehe CONCEPT.md 6.12.</summary>
    public int? MaxRouteVariantAttempts { get; init; }

    /// <summary>Vom Nutzer auf der Karte markierte Bereiche, die bei JEDER Routenberechnung
    /// (round_trip UND Wegpunkt-Routing) komplett gemieden werden sollen, siehe CONCEPT.md
    /// Abschnitt 6.18. Leer = keine Sperrungen.</summary>
    public IReadOnlyList<BlockedArea> BlockedAreas { get; init; } = [];
}
