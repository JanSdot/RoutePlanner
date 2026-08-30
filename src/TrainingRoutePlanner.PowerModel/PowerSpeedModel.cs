using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.PowerModel;

/// <summary>
/// Steady-state Watt-zu-Geschwindigkeit-Modell, siehe CONCEPT.md Abschnitt 3.3.
/// Keine Beschleunigungs-/Ausrollphasen, kein Wind (siehe 3.3/3.4/Roadmap 6 Phase 4) -
/// bewusste Vereinfachungen fuer den Prototyp, gelten auch fuer Sprint.
/// </summary>
public sealed class PowerSpeedModel
{
    // Meereshoehe, ~15 C - fuer den Prototyp als Konstante, keine Wetter-/Hoehenkorrektur (siehe 3.3).
    private const double AirDensityKgM3 = 1.225;
    private const double GravityMs2 = 9.80665;

    private const int MaxIterations = 50;
    private const double ConvergenceToleranceWatts = 1e-6;
    private const double InitialGuessMps = 5.0;

    /// <summary>
    /// Loest die Geschwindigkeit (m/s) fuer eine gegebene Fahrerleistung numerisch via
    /// Newton-Raphson. P_luft skaliert mit v^3, daher existiert keine geschlossene Umkehrformel
    /// (siehe CONCEPT.md 3.3). f(v) ist fuer v >= 0 monoton steigend, das Verfahren konvergiert
    /// daher zuverlaessig von einem festen Startwert aus.
    /// </summary>
    /// <param name="riderPowerWatts">Vom Fahrer an den Pedalen erzeugte Leistung (Watt).</param>
    /// <param name="profile">Fahrerprofil (Gewicht, CdA, Crr, Antriebswirkungsgrad).</param>
    /// <param name="gradient">
    /// Steigung als Bruchteil (z. B. 0.03 fuer 3%), nicht als Winkel. Wird intern via atan()
    /// in einen Winkel umgerechnet, siehe CONCEPT.md 3.3.
    /// </param>
    /// <returns>Geschwindigkeit in m/s.</returns>
    public double SolveSpeedMps(double riderPowerWatts, RiderProfile profile, double gradient = 0.0)
    {
        ArgumentNullException.ThrowIfNull(profile);

        double effectivePowerWatts = riderPowerWatts * profile.DrivetrainEfficiency;

        // Kein Antrieb bzw. negative Leistung: kein Vorwaertsfahren moeglich. Ebenso deckt dies
        // den Fall ab, dass an sehr steilen Anstiegen selbst bei v->0 die Steigungs-/Rollleistung
        // die verfuegbare Leistung uebersteigt (siehe Aufgabenstellung: kein Sonderfall fuer
        // "unmoegliche" Steigungen, nur dokumentiertes Verhalten) - wir geben schlicht 0 zurueck.
        if (effectivePowerWatts <= 0.0)
        {
            return 0.0;
        }

        double slopeAngleRad = Math.Atan(gradient);
        double cosAngle = Math.Cos(slopeAngleRad);
        double sinAngle = Math.Sin(slopeAngleRad);

        double m = profile.WeightKg;
        double crr = profile.RollingResistanceCoefficient;
        double cdA = profile.DragAreaCdA;

        double rollAndClimbCoefficient = m * GravityMs2 * (crr * cosAngle + sinAngle);

        double v = InitialGuessMps;

        for (int i = 0; i < MaxIterations; i++)
        {
            double airPower = 0.5 * AirDensityKgM3 * cdA * v * v * v;
            double rollClimbPower = rollAndClimbCoefficient * v;
            double f = airPower + rollClimbPower - effectivePowerWatts;

            if (Math.Abs(f) < ConvergenceToleranceWatts)
            {
                break;
            }

            double fPrime = 1.5 * AirDensityKgM3 * cdA * v * v + rollAndClimbCoefficient;

            if (fPrime <= 0.0)
            {
                break;
            }

            double next = v - f / fPrime;

            // Newton-Schritt kann bei ungluecklichem Start ins Negative laufen (kubischer Term);
            // an der Nullstelle klemmen statt divergieren zu lassen.
            v = next < 0.0 ? 0.0 : next;
        }

        return v;
    }

    /// <summary>
    /// Zeit, um eine gegebene Distanz bei konstanter Leistung und Steigung zurueckzulegen.
    /// Baut auf <see cref="SolveSpeedMps"/> auf (steady state, siehe CONCEPT.md 3.3).
    /// </summary>
    /// <param name="distanceMeters">Zu fahrende Distanz in Metern.</param>
    /// <param name="riderPowerWatts">Vom Fahrer an den Pedalen erzeugte Leistung (Watt).</param>
    /// <param name="profile">Fahrerprofil.</param>
    /// <param name="gradient">Steigung als Bruchteil (z. B. 0.03 fuer 3%).</param>
    /// <returns>Benoetigte Zeit als <see cref="TimeSpan"/>.</returns>
    public TimeSpan TimeForDistance(double distanceMeters, double riderPowerWatts, RiderProfile profile, double gradient = 0.0)
    {
        double speedMps = SolveSpeedMps(riderPowerWatts, profile, gradient);

        if (speedMps <= 0.0)
        {
            return TimeSpan.MaxValue;
        }

        double seconds = distanceMeters / speedMps;
        return TimeSpan.FromSeconds(seconds);
    }
}
