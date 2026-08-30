using Dynastream.Fit;
using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.FitParsing;

/// <summary>Ein einzelner Schritt im manuellen Block-Editor, siehe CONCEPT.md Abschnitt 6
/// Phase 4 ("Manuelle Block-Builder-UI als Alternative zum FIT-Import").</summary>
public sealed record WorkoutStepSpec(TrainingZone Zone, int DurationMinutes);

/// <summary>Entweder ein einzelner Schritt oder eine Wiederholungsgruppe - mappt auf FITs
/// eigenes repeat_steps-Konzept (siehe FitWorkoutEncoder.Encode).</summary>
public sealed record WorkoutBlockSpec(
    WorkoutStepSpec? Step = null,
    int? RepeatTimes = null,
    IReadOnlyList<WorkoutStepSpec>? RepeatSteps = null);

/// <summary>Erzeugt aus einer Blockliste eine echte FIT-Workout-Datei (Gegenstueck zu
/// FitWorkoutParser). Zielleistung wird bewusst als %FTP-Bereich kodiert (nicht als absolute
/// Watt) - das macht die generierte Datei nutzerunabhaengig wiederverwendbar, da
/// FitWorkoutParser das %FTP-Ziel beim Einlesen erst mit dem jeweiligen Nutzerprofil auflöst
/// (siehe CONCEPT.md 3.2/3.4). Sprint wird deshalb hier NICHT unterstuetzt, da Sprint absichtlich
/// nicht %FTP-basiert ist (siehe ZoneBands) - Einschraenkung, siehe CONCEPT.md Abschnitt 7.</summary>
public static class FitWorkoutEncoder
{
    public static byte[] Encode(IReadOnlyList<WorkoutBlockSpec> blocks, string workoutName = "Generiertes Workout")
    {
        if (blocks.Any(b => b.Step?.Zone == TrainingZone.Sprint || (b.RepeatSteps?.Any(s => s.Zone == TrainingZone.Sprint) ?? false)))
            throw new NotSupportedException("Sprint wird im Block-Editor nicht unterstuetzt (nicht %FTP-basiert).");

        var steps = new List<WorkoutStepMesg>();
        ushort messageIndex = 0;

        foreach (var block in blocks)
        {
            if (block.Step is { } step)
            {
                steps.Add(BuildTimeStep(messageIndex++, step));
            }
            else if (block.RepeatSteps is { Count: > 0 } repeatSteps && block.RepeatTimes is { } times)
            {
                var firstIndex = messageIndex;
                foreach (var inner in repeatSteps)
                    steps.Add(BuildTimeStep(messageIndex++, inner));

                var repeatMarker = new WorkoutStepMesg();
                repeatMarker.SetMessageIndex(messageIndex++);
                repeatMarker.SetDurationType(WktStepDuration.RepeatUntilStepsCmplt);
                repeatMarker.SetDurationValue(firstIndex);
                repeatMarker.SetTargetType(WktStepTarget.Open);
                repeatMarker.SetTargetValue((uint)times);
                // Siehe FitWorkoutParserTests: ein Schritt ohne wkt_step_name korrumpiert beim
                // Dekodieren (Garmin.FIT.Sdk 21.214.0) die Namen ALLER Schritte der Datei.
                repeatMarker.SetWktStepName("(Wiederholung)");
                steps.Add(repeatMarker);
            }
            else
            {
                throw new ArgumentException("Block muss entweder Step oder RepeatTimes+RepeatSteps setzen.");
            }
        }

        var workoutMesg = new WorkoutMesg();
        workoutMesg.SetWktName(workoutName);
        workoutMesg.SetSport(Sport.Cycling);
        workoutMesg.SetSubSport(SubSport.Invalid);
        workoutMesg.SetNumValidSteps((ushort)steps.Count);

        using var stream = new MemoryStream();
        var fileIdMesg = new FileIdMesg();
        fileIdMesg.SetType(Dynastream.Fit.File.Workout);
        fileIdMesg.SetManufacturer(Manufacturer.Development);
        fileIdMesg.SetProduct(0);
        fileIdMesg.SetTimeCreated(new Dynastream.Fit.DateTime(System.DateTime.UtcNow));
        fileIdMesg.SetSerialNumber(1u);

        var encoder = new Encode(ProtocolVersion.V10);
        encoder.Open(stream);
        encoder.Write(fileIdMesg);
        encoder.Write(workoutMesg);
        foreach (var step in steps)
            encoder.Write(step);
        encoder.Close();

        return stream.ToArray();
    }

    private static WorkoutStepMesg BuildTimeStep(ushort messageIndex, WorkoutStepSpec spec)
    {
        var band = ZoneBands.ForZone(spec.Zone);
        var upperPercent = double.IsPositiveInfinity(band.FtpPercentHigh) || band.FtpPercentHigh == double.MaxValue
            ? band.FtpPercentLow + 15
            : band.FtpPercentHigh;

        var step = new WorkoutStepMesg();
        step.SetMessageIndex(messageIndex);
        step.SetDurationType(WktStepDuration.Time);
        step.SetDurationValue((uint)(spec.DurationMinutes * 60 * 1000));
        step.SetWktStepName(spec.Zone.ToString());
        step.SetTargetType(WktStepTarget.Power);
        step.SetTargetValue(0);
        // raw < 1000 = woertliches %FTP, siehe FitWorkoutParser/WorkoutPower.WattsOffset.
        step.SetCustomTargetPowerLow((uint)band.FtpPercentLow);
        step.SetCustomTargetPowerHigh((uint)upperPercent);
        return step;
    }
}
