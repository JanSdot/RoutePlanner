using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.FitParsing;
using Xunit;

namespace TrainingRoutePlanner.Tests;

/// <summary>Rundreise-Tests: FitWorkoutEncoder erzeugt echte FIT-Bytes, FitWorkoutParser liest
/// sie zurueck ein - stellt sicher, dass der manuelle Block-Editor (Encoder) exakt das erzeugt,
/// was der bestehende Import-Pfad (Parser) erwartet.</summary>
public class FitWorkoutEncoderTests
{
    private static RiderProfile CreateProfile() => new()
    {
        FtpWatts = 250,
        WeightKg = 75,
        SprintAvgWatts = 900,
    };

    [Fact]
    public void Encode_ThenParse_RoundTripsPlainSteps()
    {
        var blocks = new List<WorkoutBlockSpec>
        {
            new(Step: new WorkoutStepSpec(TrainingZone.GA1, 20)),
            new(Step: new WorkoutStepSpec(TrainingZone.EB, 5)),
        };

        var bytes = FitWorkoutEncoder.Encode(blocks);
        using var stream = new MemoryStream(bytes);
        var plan = new FitWorkoutParser().ParseWorkout(stream, CreateProfile());

        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(TimeSpan.FromMinutes(20), plan.Steps[0].Duration);
        Assert.Equal("GA1", plan.Steps[0].Label);
        Assert.Equal(TimeSpan.FromMinutes(5), plan.Steps[1].Duration);
        Assert.Equal("EB", plan.Steps[1].Label);
        // EB-Band 90-100% FTP, Encoder nutzt die Bandgrenzen direkt -> Mittelwert 95% von 250W.
        Assert.Equal(237.5, plan.Steps[1].TargetPowerWatts, precision: 1);
    }

    [Fact]
    public void Encode_ThenParse_RoundTripsRepeatGroup()
    {
        var blocks = new List<WorkoutBlockSpec>
        {
            new(Step: new WorkoutStepSpec(TrainingZone.GA1, 20)),
            new(RepeatTimes: 3, RepeatSteps:
            [
                new WorkoutStepSpec(TrainingZone.EB, 5),
                new WorkoutStepSpec(TrainingZone.GA1, 3),
            ]),
        };

        var bytes = FitWorkoutEncoder.Encode(blocks);
        using var stream = new MemoryStream(bytes);
        var plan = new FitWorkoutParser().ParseWorkout(stream, CreateProfile());

        // 1 Warmup + 3 * (1 Work + 1 Recovery) = 7 Schritte.
        Assert.Equal(7, plan.Steps.Count);
        for (var rep = 0; rep < 3; rep++)
        {
            var work = plan.Steps[1 + rep * 2];
            var recovery = plan.Steps[2 + rep * 2];
            Assert.Equal("EB", work.Label);
            Assert.Equal(TimeSpan.FromMinutes(5), work.Duration);
            Assert.Equal("GA1", recovery.Label);
            Assert.Equal(TimeSpan.FromMinutes(3), recovery.Duration);
        }
    }

    [Fact]
    public void Encode_RejectsSprintZone()
    {
        var blocks = new List<WorkoutBlockSpec>
        {
            new(Step: new WorkoutStepSpec(TrainingZone.Sprint, 1)),
        };

        Assert.Throws<NotSupportedException>(() => FitWorkoutEncoder.Encode(blocks));
    }
}
