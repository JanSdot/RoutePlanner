namespace TrainingRoutePlanner.FitParsing;

/// <summary>Thrown for any FIT workout file that FitWorkoutParser cannot turn into a
/// TrainingPlan - malformed/corrupt files, missing workout_step data, or FIT constructs
/// that are deliberately out of scope for the Phase-1 MVP (see FitWorkoutParser).</summary>
public sealed class FitParsingException : Exception
{
    public FitParsingException(string message) : base(message)
    {
    }

    public FitParsingException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
