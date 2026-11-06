namespace UrevoSpacewalkCommander.Workouts;

/// <summary>
/// State change of the belt during a workout.
/// </summary>
internal enum WorkoutEventKind
{
  /// <summary>The workout began.</summary>
  Start,

  /// <summary>The belt was paused.</summary>
  Pause,

  /// <summary>The belt was resumed after a pause.</summary>
  Resume,

  /// <summary>The workout ended.</summary>
  Stop,
}

/// <summary>
/// A state change of the belt at a point in time.
/// </summary>
/// <param name="Time">When the change happened (local time with offset).</param>
/// <param name="Kind">What happened.</param>
internal sealed record WorkoutEvent(DateTimeOffset Time, WorkoutEventKind Kind);
