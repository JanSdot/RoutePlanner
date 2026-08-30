using Dynastream.Fit;
using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.FitParsing;

namespace TrainingRoutePlanner.Tests;

/// <summary>
/// Builds FIT workout files with the SDK's own Encode API (mirrors
/// fit-csharp-sdk/Cookbook/WorkoutEncode/Program.cs) rather than depending on an external
/// sample .fit file, so these tests are self-contained and don't trust a binary from an
/// untrusted source. This exercises the full round-trip (encode -> decode -> flatten ->
/// resolve), which is stronger evidence than mocking the decode side, since Garmin.FIT.Sdk's
/// Mesg/Field/subfield-activation machinery is itself part of what we're relying on.
/// </summary>
public class FitWorkoutParserTests
{
    private static RiderProfile CreateProfile() => new()
    {
        FtpWatts = 250,
        WeightKg = 75.0,
        SprintAvgWatts = 900,
    };

    private static MemoryStream EncodeWorkout(WorkoutMesg workoutMesg, List<WorkoutStepMesg> steps)
    {
        var stream = new MemoryStream();
        var fileIdMesg = new FileIdMesg();
        fileIdMesg.SetType(Dynastream.Fit.File.Workout);
        fileIdMesg.SetManufacturer(Manufacturer.Development);
        fileIdMesg.SetProduct(0);
        fileIdMesg.SetTimeCreated(new Dynastream.Fit.DateTime(System.DateTime.UtcNow));
        fileIdMesg.SetSerialNumber(12345u);

        var encoder = new Encode(ProtocolVersion.V10);
        encoder.Open(stream);
        encoder.Write(fileIdMesg);
        encoder.Write(workoutMesg);
        foreach (var step in steps)
        {
            encoder.Write(step);
        }
        encoder.Close();

        stream.Position = 0;
        return stream;
    }

    private static WorkoutStepMesg CreateTimeStep(int messageIndex, uint durationSeconds, uint? lowWatts = null, uint? highWatts = null, string? name = null)
    {
        var step = new WorkoutStepMesg();
        step.SetMessageIndex((ushort)messageIndex);
        step.SetDurationType(WktStepDuration.Time);
        step.SetDurationValue(durationSeconds * 1000); // milliseconds, per WorkoutStepMesg field docs

        if (name != null)
        {
            step.SetWktStepName(name);
        }

        if (lowWatts.HasValue && highWatts.HasValue)
        {
            step.SetTargetType(WktStepTarget.Power);
            step.SetTargetValue(0);
            step.SetCustomTargetPowerLow(lowWatts.Value + WorkoutPower.WattsOffset);
            step.SetCustomTargetPowerHigh(highWatts.Value + WorkoutPower.WattsOffset);
        }
        else
        {
            step.SetTargetType(WktStepTarget.Open);
        }

        return step;
    }

    private static WorkoutStepMesg CreateRepeatStep(int messageIndex, uint repeatFromMessageIndex, uint repetitions)
    {
        var step = new WorkoutStepMesg();
        step.SetMessageIndex((ushort)messageIndex);
        step.SetDurationType(WktStepDuration.RepeatUntilStepsCmplt);
        step.SetDurationValue(repeatFromMessageIndex);
        step.SetTargetType(WktStepTarget.Open);
        step.SetTargetValue(repetitions);

        // Empirically confirmed (not guessed) SDK quirk in Garmin.FIT.Sdk 21.214.0: if one
        // workout_step in a file omits wkt_step_name (field 0) while sibling steps set it,
        // Decode corrupts the wkt_step_name STRING field for every step of that message type in
        // the file - including ones already decoded earlier in the stream - even though the raw
        // encoded bytes on disk are correct (verified with a hex dump). Numeric fields are
        // unaffected. Repro: encode 3 named Time steps + 1 unnamed RepeatUntilStepsCmplt marker,
        // round-trip through a real file (not just a MemoryStream, to rule out stream-position
        // artifacts) via Decode+FitListener - all four GetWktStepNameAsString() calls return
        // null-byte garbage instead of the encoded names. Giving the marker step a name of its
        // own (unused by FitWorkoutParser, which never reads a repeat marker's Label) avoids
        // triggering it, so we do that here purely to keep this test's Label assertions
        // meaningful. See the FitWorkoutParser.ResolveStep Label comment and the final task
        // report for the full write-up; this is flagged as an unverified-in-the-wild SDK gap.
        step.SetWktStepName("(repeat)");
        return step;
    }

    [Fact]
    public void ParseWorkout_UnrollsRepeatBlock_IntoExpectedFlatStepSequence()
    {
        // 20 min warmup (absolute watts) then 3x(5 min work, 3 min recovery), matching the
        // scenario from the task brief: 1 + 3*(1+1) = 7 flattened steps.
        var steps = new List<WorkoutStepMesg>
        {
            CreateTimeStep(0, durationSeconds: 20 * 60, lowWatts: 130, highWatts: 150, name: "Warmup"),
            CreateTimeStep(1, durationSeconds: 5 * 60, lowWatts: 290, highWatts: 300, name: "Work"),
            CreateTimeStep(2, durationSeconds: 3 * 60, lowWatts: 90, highWatts: 100, name: "Recovery"),
            CreateRepeatStep(3, repeatFromMessageIndex: 1, repetitions: 3),
        };

        var workoutMesg = new WorkoutMesg();
        workoutMesg.SetWktName("Test Interval Workout");
        workoutMesg.SetSport(Sport.Cycling);
        workoutMesg.SetSubSport(SubSport.Invalid);
        workoutMesg.SetNumValidSteps((ushort)steps.Count);

        using var stream = EncodeWorkout(workoutMesg, steps);

        var profile = CreateProfile();
        var plan = new FitWorkoutParser().ParseWorkout(stream, profile);

        Assert.Equal(7, plan.Steps.Count);

        Assert.Equal("Warmup", plan.Steps[0].Label);
        Assert.Equal(TimeSpan.FromMinutes(20), plan.Steps[0].Duration);
        Assert.Equal(140.0, plan.Steps[0].TargetPowerWatts, precision: 3);

        for (var rep = 0; rep < 3; rep++)
        {
            var work = plan.Steps[1 + rep * 2];
            var recovery = plan.Steps[2 + rep * 2];

            Assert.Equal("Work", work.Label);
            Assert.Equal(TimeSpan.FromMinutes(5), work.Duration);
            Assert.Equal(295.0, work.TargetPowerWatts, precision: 3);

            Assert.Equal("Recovery", recovery.Label);
            Assert.Equal(TimeSpan.FromMinutes(3), recovery.Duration);
            Assert.Equal(95.0, recovery.TargetPowerWatts, precision: 3);
        }

        // Sanity check that resolved disruption scores follow CONCEPT.md 3.4: the high-power
        // work interval must be much less tolerant of interruptions than warmup/recovery.
        Assert.True(plan.Steps[1].MaxDisruptionScore < plan.Steps[0].MaxDisruptionScore);
        Assert.True(plan.Steps[1].MaxDisruptionScore < plan.Steps[2].MaxDisruptionScore);
    }

    [Fact]
    public void ParseWorkout_DecodesPercentFtpCustomTarget_AsFractionOfProfileFtp()
    {
        var step = new WorkoutStepMesg();
        step.SetMessageIndex(0);
        step.SetDurationType(WktStepDuration.Time);
        step.SetDurationValue(10 * 60 * 1000);
        step.SetTargetType(WktStepTarget.Power);
        step.SetTargetValue(0);
        step.SetCustomTargetPowerLow(60); // 60% FTP (below WorkoutPower.WattsOffset => percent)
        step.SetCustomTargetPowerHigh(70); // 70% FTP

        var workoutMesg = new WorkoutMesg();
        workoutMesg.SetWktName("Percent FTP Workout");
        workoutMesg.SetSport(Sport.Cycling);
        workoutMesg.SetSubSport(SubSport.Invalid);
        workoutMesg.SetNumValidSteps(1);

        using var stream = EncodeWorkout(workoutMesg, new List<WorkoutStepMesg> { step });

        var profile = CreateProfile(); // FtpWatts = 250
        var plan = new FitWorkoutParser().ParseWorkout(stream, profile);

        Assert.Single(plan.Steps);
        // midpoint 65% of 250W = 162.5W
        Assert.Equal(162.5, plan.Steps[0].TargetPowerWatts, precision: 3);
    }

    [Fact]
    public void ParseWorkout_FallsBackToGa1_ForHeartRateTargetStep()
    {
        var step = new WorkoutStepMesg();
        step.SetMessageIndex(0);
        step.SetDurationType(WktStepDuration.Time);
        step.SetDurationValue(10 * 60 * 1000);
        step.SetTargetType(WktStepTarget.HeartRate);
        step.SetTargetValue(1);

        var workoutMesg = new WorkoutMesg();
        workoutMesg.SetWktName("HR Target Workout");
        workoutMesg.SetSport(Sport.Cycling);
        workoutMesg.SetSubSport(SubSport.Invalid);
        workoutMesg.SetNumValidSteps(1);

        using var stream = EncodeWorkout(workoutMesg, new List<WorkoutStepMesg> { step });

        var profile = CreateProfile();
        var plan = new FitWorkoutParser().ParseWorkout(stream, profile);

        Assert.Single(plan.Steps);
        Assert.Equal(profile.FtpWatts * 0.55, plan.Steps[0].TargetPowerWatts, precision: 3);
        Assert.Equal(ZoneBands.ForFtpPercent(55.0).MaxDisruptionScore, plan.Steps[0].MaxDisruptionScore);
    }

    [Fact]
    public void ParseWorkout_UsesFallbackDuration_ForOpenEndedStep()
    {
        var step = new WorkoutStepMesg();
        step.SetMessageIndex(0);
        step.SetDurationType(WktStepDuration.Open);
        step.SetTargetType(WktStepTarget.Open);
        step.SetIntensity(Intensity.Cooldown);

        var workoutMesg = new WorkoutMesg();
        workoutMesg.SetWktName("Open Step Workout");
        workoutMesg.SetSport(Sport.Cycling);
        workoutMesg.SetSubSport(SubSport.Invalid);
        workoutMesg.SetNumValidSteps(1);

        using var stream = EncodeWorkout(workoutMesg, new List<WorkoutStepMesg> { step });

        var plan = new FitWorkoutParser().ParseWorkout(stream, CreateProfile());

        Assert.Single(plan.Steps);
        Assert.Equal(TimeSpan.FromMinutes(5), plan.Steps[0].Duration);
    }

    [Fact]
    public void ParseWorkout_ThrowsNotSupported_ForDistanceBasedStep()
    {
        var step = new WorkoutStepMesg();
        step.SetMessageIndex(0);
        step.SetDurationType(WktStepDuration.Distance);
        step.SetDurationDistance(5000f);
        step.SetTargetType(WktStepTarget.Open);

        var workoutMesg = new WorkoutMesg();
        workoutMesg.SetWktName("Distance Step Workout");
        workoutMesg.SetSport(Sport.Cycling);
        workoutMesg.SetSubSport(SubSport.Invalid);
        workoutMesg.SetNumValidSteps(1);

        using var stream = EncodeWorkout(workoutMesg, new List<WorkoutStepMesg> { step });

        Assert.Throws<NotSupportedException>(() => new FitWorkoutParser().ParseWorkout(stream, CreateProfile()));
    }

    [Fact]
    public void ParseWorkout_Throws_ForEmptyWorkout()
    {
        var workoutMesg = new WorkoutMesg();
        workoutMesg.SetWktName("Empty Workout");
        workoutMesg.SetSport(Sport.Cycling);
        workoutMesg.SetSubSport(SubSport.Invalid);
        workoutMesg.SetNumValidSteps(0);

        using var stream = EncodeWorkout(workoutMesg, new List<WorkoutStepMesg>());

        Assert.Throws<FitParsingException>(() => new FitWorkoutParser().ParseWorkout(stream, CreateProfile()));
    }
}
