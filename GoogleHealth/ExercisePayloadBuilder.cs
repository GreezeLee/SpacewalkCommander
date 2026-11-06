using System.Globalization;
using SpacewalkCommander.Workouts;

namespace SpacewalkCommander.GoogleHealth;

/// <summary>
/// Maps a recorded workout to the Google Health API exercise data point.
/// </summary>
internal static class ExercisePayloadBuilder
{
  /// <summary>Activity type of a walk on a treadmill.</summary>
  public const string ExerciseType = "TREADMILL_WALK";

  /// <summary>The treadmill measured the data while the user exercised.</summary>
  private const string RecordingMethod = "ACTIVELY_MEASURED";

  /// <summary>Manufacturer shown with the data source.</summary>
  private const string Manufacturer = "Urevo";

  /// <summary>Name sent as display name; the API replaces it with a generated one for TREADMILL_WALK.</summary>
  private const string DisplayName = "Treadmill walk";

  /// <summary>Splits are delimited by distance (one per kilometre).</summary>
  private const string DistanceSplitType = "DISTANCE";

  /// <summary>Millimetres per metre.</summary>
  private const double MillimetersPerMeter = 1000;

  /// <summary>RFC 3339 format of the API's timestamps, always in UTC.</summary>
  private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

  /// <summary>
  /// Builds the data point.
  /// </summary>
  /// <param name="session">The recorded workout.</param>
  /// <param name="summary">Its key figures.</param>
  /// <returns>The request body for the exercise create call.</returns>
  public static ExerciseDataPointDto Build(WorkoutSession session, WorkoutSummary summary)
  {
    var exercise = new ExerciseDto(
      new SessionIntervalDto(
        FormatTime(session.StartTime),
        FormatOffset(session.StartTime),
        FormatTime(session.EndTime),
        FormatOffset(session.EndTime)),
      ExerciseType,
      DisplayName,
      FormatDuration(summary.ActiveDuration),
      BuildMetrics(summary),
      [.. session.Events.Select(BuildEvent)],
      [.. summary.Splits.Select(BuildSplit)],
      BuildNotes(session, summary));
    var device = new DeviceDto(Manufacturer, string.IsNullOrWhiteSpace(session.DeviceName) ? null : session.DeviceName);
    return new ExerciseDataPointDto(new DataSourceDto(RecordingMethod, device), exercise);
  }

  /// <summary>
  /// Formats a time as RFC 3339 in UTC.
  /// </summary>
  /// <param name="time">The time.</param>
  /// <returns>For example <c>2026-11-06T17:30:00.000Z</c>.</returns>
  public static string FormatTime(DateTimeOffset time)
  {
    return time.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Formats the UTC offset of a time as API duration.
  /// </summary>
  /// <param name="time">The local time.</param>
  /// <returns>For example <c>"3600s"</c> for UTC+1.</returns>
  public static string FormatOffset(DateTimeOffset time)
  {
    return FormatDuration(time.Offset);
  }

  /// <summary>
  /// Formats a duration in whole seconds as API duration.
  /// </summary>
  /// <param name="duration">The duration.</param>
  /// <returns>For example <c>"1800s"</c>.</returns>
  public static string FormatDuration(TimeSpan duration)
  {
    return string.Create(CultureInfo.InvariantCulture, $"{(long)Math.Round(duration.TotalSeconds)}s");
  }

  /// <summary>
  /// Builds the session totals. Speed and pace are based on the active duration, as the API guide requires.
  /// </summary>
  /// <param name="summary">The key figures.</param>
  /// <returns>The metrics.</returns>
  private static MetricsSummaryDto BuildMetrics(WorkoutSummary summary)
  {
    double activeSeconds = summary.ActiveDuration.TotalSeconds;
    double distanceMillimeters = summary.DistanceMeters * MillimetersPerMeter;
    return new MetricsSummaryDto(
      RoundOrNull(summary.EnergyKcal, 1),
      Math.Round(distanceMillimeters),
      activeSeconds > 0 ? Math.Round(distanceMillimeters / activeSeconds, 2) : null,
      summary.DistanceMeters > 0 ? Math.Round(activeSeconds / summary.DistanceMeters, 3) : null,
      summary.AverageHeartRate?.ToString(CultureInfo.InvariantCulture),
      summary.ElevationGainMeters > 0 ? Math.Round(summary.ElevationGainMeters * MillimetersPerMeter) : null);
  }

  /// <summary>
  /// Maps a recorded event.
  /// </summary>
  /// <param name="workoutEvent">The event.</param>
  /// <returns>The API event.</returns>
  private static ExerciseEventDto BuildEvent(WorkoutEvent workoutEvent)
  {
    string type = workoutEvent.Kind switch
    {
      WorkoutEventKind.Start => "START",
      WorkoutEventKind.Pause => "PAUSE",
      WorkoutEventKind.Resume => "RESUME",
      WorkoutEventKind.Stop => "STOP",
      _ => throw new ArgumentOutOfRangeException(nameof(workoutEvent), workoutEvent.Kind, "Unknown event kind."),
    };
    return new ExerciseEventDto(FormatTime(workoutEvent.Time), FormatOffset(workoutEvent.Time), type);
  }

  /// <summary>
  /// Maps a split.
  /// </summary>
  /// <param name="split">The split.</param>
  /// <returns>The API split summary.</returns>
  private static SplitSummaryDto BuildSplit(WorkoutSplit split)
  {
    var metrics = new MetricsSummaryDto(
      RoundOrNull(split.EnergyKcal, 1),
      Math.Round(split.DistanceMeters * MillimetersPerMeter),
      null,
      null,
      null,
      null);
    return new SplitSummaryDto(
      FormatTime(split.StartTime),
      FormatOffset(split.StartTime),
      FormatTime(split.EndTime),
      FormatOffset(split.EndTime),
      DistanceSplitType,
      metrics);
  }

  /// <summary>
  /// Writes the figures the API has no field for (maximum speed, incline) into the notes.
  /// </summary>
  /// <param name="session">The workout.</param>
  /// <param name="summary">Its key figures.</param>
  /// <returns>The notes text.</returns>
  private static string BuildNotes(WorkoutSession session, WorkoutSummary summary)
  {
    CultureInfo culture = CultureInfo.InvariantCulture;
    string energy = summary.EnergyKcal is { } kcal
      ? string.Create(culture, $", {kcal:0} kcal{(summary.EnergyEstimated ? " (estimated)" : string.Empty)}")
      : string.Empty;
    return string.Create(
      culture,
      $"{session.DeviceName}: avg {summary.AverageSpeedKmh:0.0} km/h (max {summary.MaxSpeedKmh:0.0}), "
      + $"avg incline {summary.AverageInclinePercent:0.0} % (max {summary.MaxInclinePercent:0.#} %), "
      + $"{summary.ElevationGainMeters:0} m climb{energy}");
  }

  /// <summary>
  /// Rounds an optional value.
  /// </summary>
  /// <param name="value">The value.</param>
  /// <param name="digits">Decimal places.</param>
  /// <returns>The rounded value, or <see langword="null"/>.</returns>
  private static double? RoundOrNull(double? value, int digits)
  {
    return value is { } number ? Math.Round(number, digits) : null;
  }
}
