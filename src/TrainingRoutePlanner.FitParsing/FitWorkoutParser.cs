using Dynastream.Fit;
using TrainingRoutePlanner.Domain;

namespace TrainingRoutePlanner.FitParsing;

/// <summary>
/// Parses a FIT workout file (Dynastream.Fit.File.Workout) into a fully unrolled
/// TrainingRoutePlanner.Domain.TrainingPlan - see CONCEPT.md Abschnitt 5. FTP always comes
/// from the caller-supplied RiderProfile, never from the FIT file itself.
///
/// FIT field semantics used here were verified directly against the Garmin.FIT.Sdk
/// 21.214.0 package (decompiled/reflected, since the NuGet package ships only the
/// compiled FitSDK.dll) and the official garmin/fit-csharp-sdk GitHub repo sources
/// (Dynastream/Fit/Profile/Mesgs/WorkoutStepMesg.cs, Profile/Types/WorkoutPower.cs, and
/// the Cookbook/WorkoutEncode/Program.cs example) - not from memory/guesswork. See the
/// per-field comments below for what was confirmed where.
/// </summary>
public sealed class FitWorkoutParser
{
    /// <summary>
    /// WktStepTarget != Power (HeartRate/Speed/Cadence/Open/Grade/...), and Power targets
    /// that use a device-local power *zone index* (1-7, see WorkoutStepMesg.GetTargetPowerZone
    /// doc comment "Power Zone (1-7); Custom = 0") rather than an explicit custom watt/%FTP
    /// range, cannot be resolved to a concrete watt target without data this project does not
    /// have (an HR-power model, a speed-power model, or the device's private zone table).
    /// Rather than fail the whole workout on a single such step, or silently invent a plausible
    /// number, we fall back to a fixed conservative low-intensity target (mid-GA1) - this keeps
    /// the resulting corridor disruption-tolerance loose (GA1 tolerates almost anything, see
    /// ZoneBands.Default) instead of accidentally treating an unknown step as if it required a
    /// pristine VO2max corridor. This is a deliberate, documented placeholder for the Phase-1
    /// MVP, consistent with the "Platzhalter-Werte" note on ZoneBands.Default.
    /// </summary>
    private const double FallbackGa1FtpPercent = 55.0;

    /// <summary>
    /// WktStepDuration.Open means "no fixed duration, athlete ends the step manually" - this is
    /// not a decoding edge case, it is how Garmin's own SDK examples encode a free-form
    /// cool-down step (see fit-csharp-sdk Cookbook/WorkoutEncode/Program.cs,
    /// CreateBikeTempoWorkout's final step, which omits durationType entirely and relies on the
    /// Open default). Since route planning needs *some* time/distance budget up front, we
    /// substitute a fixed placeholder rather than throwing on a construct that real-world
    /// workout files use routinely, or silently producing a zero-length step that would vanish
    /// from the route entirely.
    /// </summary>
    private static readonly TimeSpan OpenStepFallbackDuration = TimeSpan.FromMinutes(5);

    public TrainingPlan ParseWorkout(Stream fitFileStream, RiderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(fitFileStream);
        ArgumentNullException.ThrowIfNull(profile);

        var listener = new FitListener();
        var decoder = new Decode();
        decoder.MesgEvent += listener.OnMesg;

        bool decodedOk;
        try
        {
            // Decode/FitListener/FitMessages is the exact pattern from fit-csharp-sdk's own
            // Examples/Decode/DecodeDemo.cs - confirmed present in the shipped Garmin.FIT.Sdk
            // assembly via reflection (Dynastream.Fit.FitListener, .FitMessages both exist in
            // FitSDK.dll, not just in the example source tree).
            decodedOk = decoder.Read(fitFileStream);
        }
        catch (FitException ex)
        {
            throw new FitParsingException($"FIT decode failed: {ex.Message}", ex);
        }

        if (!decodedOk)
        {
            throw new FitParsingException("FIT stream did not decode successfully (corrupt or invalid file).");
        }

        var fileIdMesg = listener.FitMessages.FileIdMesgs.FirstOrDefault();
        if (fileIdMesg?.GetType() is { } fileType && fileType != Dynastream.Fit.File.Workout)
        {
            throw new FitParsingException($"Expected a Workout FIT file, but file_id.type was '{fileType}'.");
        }

        var rawSteps = listener.FitMessages.WorkoutStepMesgs;
        if (rawSteps.Count == 0)
        {
            throw new FitParsingException("FIT file contains no workout_step messages.");
        }

        // message_index should already match decode/file order (that's how every FIT workout
        // encoder, including Garmin's own cookbook example, writes steps), but we sort
        // defensively since RepeatUntilStepsCmplt's "jump back to" reference is a message_index,
        // not a raw file position.
        var orderedSteps = rawSteps.All(s => s.GetMessageIndex().HasValue)
            ? rawSteps.OrderBy(s => s.GetMessageIndex()!.Value).ToList()
            : rawSteps.ToList();

        var positionByMessageIndex = new Dictionary<uint, int>();
        for (var i = 0; i < orderedSteps.Count; i++)
        {
            var messageIndex = orderedSteps[i].GetMessageIndex();
            positionByMessageIndex[messageIndex.HasValue ? messageIndex.Value : (uint)i] = i;
        }

        var unrolledSteps = FlattenRange(orderedSteps, positionByMessageIndex, 0, orderedSteps.Count, profile);

        return new TrainingPlan { Steps = unrolledSteps };
    }

    /// <summary>
    /// Unrolls repeat_steps constructs (WktStepDuration.RepeatUntilStepsCmplt) recursively:
    /// a repeat marker step's duration_value is the message_index to jump back to (see
    /// WorkoutStepMesg.GetDurationStep doc comment: "message_index of step to loop back to"),
    /// and its target_value is the repetition count (confirmed against
    /// Cookbook/WorkoutEncode/Program.cs's CreateWorkoutStepRepeat, which sets both via the raw
    /// main fields SetDurationValue/SetTargetValue rather than the RepeatSteps/DurationStep
    /// subfield setters - so we read them back the same way, via the raw GetDurationValue/
    /// GetTargetValue getters, to avoid depending on FIT subfield-activation resolution).
    /// Recursion naturally supports nested repeats, since a repeated sub-range can itself end in
    /// another repeat marker.
    /// </summary>
    private static List<TrainingStep> FlattenRange(
        List<WorkoutStepMesg> steps,
        Dictionary<uint, int> positionByMessageIndex,
        int startPos,
        int endPosExclusive,
        RiderProfile profile)
    {
        // A repeat marker's body (the steps between repeat-from and the marker itself) is
        // stored exactly once in the file - it is not also meant to run standalone before the
        // loop starts. So this needs two passes: first find every repeat marker in this range
        // and mark which positions are "body only" (only ever emitted through an expansion,
        // never as a bare single pass), then build the actual result, skipping body-only
        // positions during the plain walk and expanding them at the marker instead. A naive
        // single-pass walk that both emits step i AND expands a later marker pointing back at i
        // would double-count that step's first pass.
        var consumed = new bool[endPosExclusive - startPos];
        var repeatInfoByMarkerPos = new Dictionary<int, (int repeatFromPos, uint repetitions)>();

        for (var j = startPos; j < endPosExclusive; j++)
        {
            var durationType = steps[j].GetDurationType() ?? WktStepDuration.Open;
            if (durationType != WktStepDuration.RepeatUntilStepsCmplt)
            {
                continue;
            }

            var repeatFromMessageIndex = steps[j].GetDurationValue()
                ?? throw new FitParsingException($"Step at position {j}: repeat marker has no duration_value (repeat-from step index).");
            var repetitions = steps[j].GetTargetValue()
                ?? throw new FitParsingException($"Step at position {j}: repeat marker has no target_value (repetition count).");

            if (!positionByMessageIndex.TryGetValue(repeatFromMessageIndex, out var repeatFromPos) || repeatFromPos > j)
            {
                throw new FitParsingException(
                    $"Step at position {j}: repeat marker references message_index {repeatFromMessageIndex}, which is not an earlier step in this workout.");
            }

            repeatInfoByMarkerPos[j] = (repeatFromPos, repetitions);
            for (var p = repeatFromPos; p < j; p++)
            {
                consumed[p - startPos] = true;
            }
        }

        var result = new List<TrainingStep>();
        for (var i = startPos; i < endPosExclusive; i++)
        {
            if (repeatInfoByMarkerPos.TryGetValue(i, out var info))
            {
                for (var rep = 0; rep < info.repetitions; rep++)
                {
                    result.AddRange(FlattenRange(steps, positionByMessageIndex, info.repeatFromPos, i, profile));
                }
                continue;
            }

            if (consumed[i - startPos])
            {
                continue;
            }

            result.Add(ResolveStep(steps[i], i, profile));
        }

        return result;
    }

    private static TrainingStep ResolveStep(WorkoutStepMesg step, int stepPos, RiderProfile profile)
    {
        var duration = ResolveDuration(step, stepPos);

        // KNOWN GAP (empirically confirmed against Garmin.FIT.Sdk 21.214.0, not assumed): if any
        // workout_step in the file omits wkt_step_name entirely (a repeat_steps marker never has
        // one - see Cookbook/WorkoutEncode/Program.cs's CreateWorkoutStepRepeat, which never
        // calls SetWktStepName), Decode corrupts GetWktStepNameAsString() for every workout_step
        // in that file into null-byte garbage - including ones that DO set a name and were
        // already decoded earlier in the stream. Confirmed via a real-file (not just
        // MemoryStream) round trip and a raw hex dump showing the on-disk bytes are actually
        // correct; the corruption happens purely on the SDK's decode side. Only the string Name
        // field is affected - all numeric target/duration fields decode correctly regardless.
        // Since repeat_steps markers are a normal, common construct (CONCEPT.md Abschnitt 5), any
        // FIT workout with named steps *and* a repeat block may silently lose step Labels on
        // decode. There is no workaround available from this project's side (the corruption
        // happens inside the third-party SDK before our code sees the value), so this is
        // reported as a known limitation rather than silently trusted. Duration/TargetPowerWatts/
        // MaxDisruptionScore are unaffected - Label is purely cosmetic.
        var label = step.GetWktStepNameAsString();
        var targetType = step.GetTargetType() ?? WktStepTarget.Open;

        if (targetType == WktStepTarget.Power)
        {
            // target_value == 0 signals a custom low/high range rather than a device zone index
            // (WorkoutStepMesg.GetTargetPowerZone doc comment: "Power Zone (1-7); Custom = 0";
            // confirmed encoder-side in Cookbook/WorkoutEncode/Program.cs's CreateWorkoutStep,
            // which sets TargetValue=0 exactly when a custom low/high pair is supplied).
            var targetValue = step.GetTargetValue() ?? 0;
            if (targetValue == 0)
            {
                var low = step.GetCustomTargetPowerLow();
                var high = step.GetCustomTargetPowerHigh();
                if (IsUsableCustomTarget(low) || IsUsableCustomTarget(high))
                {
                    var wattsValues = new List<double>();
                    if (IsUsableCustomTarget(low)) wattsValues.Add(DecodeCustomPowerToWatts(low!.Value, profile));
                    if (IsUsableCustomTarget(high)) wattsValues.Add(DecodeCustomPowerToWatts(high!.Value, profile));
                    var watts = wattsValues.Average();
                    return ZoneResolver.FromAbsoluteWatts(watts, duration, profile, label);
                }
            }
        }

        return ZoneResolver.FromFtpPercent(FallbackGa1FtpPercent, duration, profile, label ?? $"GA1-Fallback({targetType})");
    }

    private static bool IsUsableCustomTarget(uint? value) => value is > 0;

    /// <summary>
    /// custom_target_power_low/high doc comment says "Units: % or watts" without further detail
    /// in the generated Mesg source; the disambiguation rule is the WorkoutPower.WattsOffset=1000
    /// constant (Dynastream/Fit/Profile/Types/WorkoutPower.cs) together with
    /// Cookbook/WorkoutEncode/Program.cs's CreateCustomTargetValuesWorkout example, which encodes
    /// 175 W / 195 W as customTargetValueLow=1175 / customTargetValueHigh=1195 (value = watts +
    /// 1000). Values below the offset are therefore %FTP encoded directly as the raw number
    /// (e.g. 65 => 65% FTP) - %FTP for cycling zones realistically never approaches 1000%, so the
    /// two ranges cannot collide.
    /// </summary>
    private static double DecodeCustomPowerToWatts(uint raw, RiderProfile profile) =>
        raw >= WorkoutPower.WattsOffset
            ? raw - WorkoutPower.WattsOffset
            : profile.FtpWatts * raw / 100.0;

    private static TimeSpan ResolveDuration(WorkoutStepMesg step, int stepPos)
    {
        var durationType = step.GetDurationType() ?? WktStepDuration.Open;
        switch (durationType)
        {
            case WktStepDuration.Time:
                // GetDurationTime() is the SDK's own scaled subfield accessor (returns seconds
                // as a float; confirmed in WorkoutStepMesg.cs - "Units: s"), so no manual
                // ms-scale math is needed here. GetDurationValue() (raw ms) is only a fallback
                // for the unlikely case a file sets the main field but the subfield read fails.
                var seconds = step.GetDurationTime();
                if (seconds.HasValue)
                {
                    return TimeSpan.FromSeconds(seconds.Value);
                }
                var rawMillis = step.GetDurationValue();
                if (rawMillis.HasValue)
                {
                    return TimeSpan.FromMilliseconds(rawMillis.Value);
                }
                throw new FitParsingException($"Step at position {stepPos}: duration_type is Time but no duration value is present.");

            case WktStepDuration.Open:
                return OpenStepFallbackDuration;

            case WktStepDuration.Distance:
                // Converting meters to a duration needs a watt->speed physical model (see
                // CONCEPT.md Abschnitt 3.3), which lives in TrainingRoutePlanner.PowerModel - a
                // project FitParsing intentionally does not depend on (see its .csproj). Rather
                // than duplicate that model or guess a nominal speed here, we treat
                // distance-based steps as out of scope for this Phase-1 MVP parser.
                throw new NotSupportedException(
                    $"Step at position {stepPos}: distance-based step durations are not supported yet (Phase-1 MVP) - needs the watt->speed model from TrainingRoutePlanner.PowerModel.");

            default:
                throw new NotSupportedException(
                    $"Step at position {stepPos}: duration_type '{durationType}' is not supported yet (Phase-1 MVP handles Time, Open, and RepeatUntilStepsCmplt only).");
        }
    }
}
