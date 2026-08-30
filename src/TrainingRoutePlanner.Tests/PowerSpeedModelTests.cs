using TrainingRoutePlanner.Domain;
using TrainingRoutePlanner.PowerModel;

namespace TrainingRoutePlanner.Tests;

public class PowerSpeedModelTests
{
    private static RiderProfile CreateProfile(double weightKg = 75.0) => new()
    {
        FtpWatts = 250,
        WeightKg = weightKg,
        SprintAvgWatts = 900,
    };

    [Fact]
    public void SolveSpeedMps_IncreasesMonotonically_WithPower()
    {
        var model = new PowerSpeedModel();
        var profile = CreateProfile();

        double[] powers = [100, 150, 200, 250, 300, 400];
        double previousSpeed = 0.0;

        foreach (double power in powers)
        {
            double speed = model.SolveSpeedMps(power, profile, gradient: 0.0);
            Assert.True(speed > previousSpeed, $"Speed at {power}W ({speed} m/s) should exceed speed at previous power ({previousSpeed} m/s)");
            previousSpeed = speed;
        }
    }

    [Fact]
    public void SolveSpeedMps_DecreasesMonotonically_WithGradient()
    {
        var model = new PowerSpeedModel();
        var profile = CreateProfile();
        const double power = 220;

        double[] gradients = [0.0, 0.02, 0.05, 0.08, 0.12];
        double previousSpeed = double.MaxValue;

        foreach (double gradient in gradients)
        {
            double speed = model.SolveSpeedMps(power, profile, gradient);
            Assert.True(speed < previousSpeed, $"Speed at gradient {gradient} ({speed} m/s) should be less than at previous gradient ({previousSpeed} m/s)");
            previousSpeed = speed;
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(225)]
    [InlineData(250)]
    public void SolveSpeedMps_FlatRoad_SanityCheck_YieldsRealisticRoadBikeSpeed(double power)
    {
        var model = new PowerSpeedModel();
        var profile = CreateProfile();

        double speedMps = model.SolveSpeedMps(power, profile, gradient: 0.0);
        double speedKmh = speedMps * 3.6;

        Assert.InRange(speedKmh, 25.0, 40.0);
    }

    [Fact]
    public void SolveSpeedMps_ZeroOrNegativePower_ReturnsZero()
    {
        var model = new PowerSpeedModel();
        var profile = CreateProfile();

        Assert.Equal(0.0, model.SolveSpeedMps(0, profile));
        Assert.Equal(0.0, model.SolveSpeedMps(-50, profile));
    }

    [Fact]
    public void TimeForDistance_IsConsistentWith_SolveSpeedMps()
    {
        var model = new PowerSpeedModel();
        var profile = CreateProfile();
        const double power = 220;
        const double gradient = 0.03;
        const double distanceMeters = 10_000;

        double speedMps = model.SolveSpeedMps(power, profile, gradient);
        TimeSpan time = model.TimeForDistance(distanceMeters, power, profile, gradient);

        double expectedSeconds = distanceMeters / speedMps;

        Assert.Equal(expectedSeconds, time.TotalSeconds, precision: 6);
    }

    [Fact]
    public void TimeForDistance_LongerDistance_TakesProportionallyLonger()
    {
        var model = new PowerSpeedModel();
        var profile = CreateProfile();
        const double power = 200;

        TimeSpan shortTime = model.TimeForDistance(5_000, power, profile);
        TimeSpan longTime = model.TimeForDistance(10_000, power, profile);

        Assert.True(Math.Abs(longTime.TotalSeconds - 2 * shortTime.TotalSeconds) < 1e-6);
    }
}
