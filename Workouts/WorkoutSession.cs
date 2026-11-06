namespace UrevoSpacewalkCommander.Workouts;

/// <summary>
/// A finished workout with its complete raw recording. This is what is archived locally and what the upload to
/// Google Health is computed from, so a failed upload can be repeated later without losing detail.
/// </summary>
/// <param name="Id">Unique id of the workout.</param>
/// <param name="DeviceName">Bluetooth name of the treadmill.</param>
/// <param name="StartTime">Start of the workout (local time with offset).</param>
/// <param name="EndTime">End of the workout (local time with offset).</param>
/// <param name="Events">Start, pause, resume and stop events in chronological order.</param>
/// <param name="Samples">Telemetry samples in chronological order.</param>
internal sealed record WorkoutSession(
  Guid Id,
  string DeviceName,
  DateTimeOffset StartTime,
  DateTimeOffset EndTime,
  IReadOnlyList<WorkoutEvent> Events,
  IReadOnlyList<WorkoutSample> Samples);
