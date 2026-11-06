namespace UrevoSpacewalkCommander.GoogleHealth;

/// <summary>
/// A Google Health API data point carrying an exercise session
/// (<c>POST /v4/users/me/dataTypes/exercise/dataPoints</c>).
/// </summary>
/// <param name="DataSource">How the data was recorded.</param>
/// <param name="Exercise">The exercise session.</param>
internal sealed record ExerciseDataPointDto(DataSourceDto DataSource, ExerciseDto Exercise);

/// <summary>
/// Origin of a data point.
/// </summary>
/// <param name="RecordingMethod">For example <c>ACTIVELY_MEASURED</c>.</param>
/// <param name="Device">The measuring device; optional.</param>
internal sealed record DataSourceDto(string RecordingMethod, DeviceDto? Device);

/// <summary>
/// The device that measured a data point. The API's form factors contain no treadmill, so none is sent.
/// </summary>
/// <param name="Manufacturer">Manufacturer of the device.</param>
/// <param name="DisplayName">Name of the device.</param>
internal sealed record DeviceDto(string? Manufacturer, string? DisplayName);

/// <summary>
/// An exercise session.
/// </summary>
/// <param name="Interval">Start and end of the session.</param>
/// <param name="ExerciseType">Activity type, for example <c>TREADMILL_WALK</c>.</param>
/// <param name="DisplayName">Required by the API, but replaced by a generated name for all types except OTHER.</param>
/// <param name="ActiveDuration">Duration without pauses, for example <c>"1800s"</c>.</param>
/// <param name="MetricsSummary">Totals and averages of the session.</param>
/// <param name="ExerciseEvents">Start, pause, resume and stop events.</param>
/// <param name="SplitSummaries">Non-overlapping sections of the session.</param>
/// <param name="Notes">Free-form notes.</param>
internal sealed record ExerciseDto(
  SessionIntervalDto Interval,
  string ExerciseType,
  string DisplayName,
  string ActiveDuration,
  MetricsSummaryDto MetricsSummary,
  IReadOnlyList<ExerciseEventDto> ExerciseEvents,
  IReadOnlyList<SplitSummaryDto> SplitSummaries,
  string? Notes);

/// <summary>
/// Start and end of a session as RFC 3339 times with the local UTC offsets as durations (<c>"3600s"</c>).
/// </summary>
/// <param name="StartTime">Start time.</param>
/// <param name="StartUtcOffset">UTC offset at the start.</param>
/// <param name="EndTime">End time.</param>
/// <param name="EndUtcOffset">UTC offset at the end.</param>
internal sealed record SessionIntervalDto(string StartTime, string StartUtcOffset, string EndTime, string EndUtcOffset);

/// <summary>
/// Totals and averages; fields that are <see langword="null"/> are omitted.
/// </summary>
/// <param name="CaloriesKcal">Energy in kcal.</param>
/// <param name="DistanceMillimeters">Distance in mm.</param>
/// <param name="AverageSpeedMillimetersPerSecond">Average speed in mm/s.</param>
/// <param name="AveragePaceSecondsPerMeter">Average pace in s/m.</param>
/// <param name="AverageHeartRateBeatsPerMinute">Average heart rate; the API expects a string.</param>
/// <param name="ElevationGainMillimeters">Climb in mm.</param>
internal sealed record MetricsSummaryDto(
  double? CaloriesKcal,
  double? DistanceMillimeters,
  double? AverageSpeedMillimetersPerSecond,
  double? AveragePaceSecondsPerMeter,
  string? AverageHeartRateBeatsPerMinute,
  double? ElevationGainMillimeters);

/// <summary>
/// A state change during the session.
/// </summary>
/// <param name="EventTime">When it happened (RFC 3339).</param>
/// <param name="EventUtcOffset">UTC offset at that time.</param>
/// <param name="ExerciseEventType">START, PAUSE, RESUME or STOP.</param>
internal sealed record ExerciseEventDto(string EventTime, string EventUtcOffset, string ExerciseEventType);

/// <summary>
/// A section (lap) of the session.
/// </summary>
/// <param name="StartTime">Start of the section.</param>
/// <param name="StartUtcOffset">UTC offset at the start.</param>
/// <param name="EndTime">End of the section.</param>
/// <param name="EndUtcOffset">UTC offset at the end.</param>
/// <param name="SplitType">How the section was delimited, here <c>DISTANCE</c>.</param>
/// <param name="MetricsSummary">Totals of the section.</param>
internal sealed record SplitSummaryDto(
  string StartTime,
  string StartUtcOffset,
  string EndTime,
  string EndUtcOffset,
  string SplitType,
  MetricsSummaryDto MetricsSummary);

/// <summary>
/// The long-running operation returned by <c>create</c>.
/// </summary>
/// <param name="Name">Name of the operation.</param>
/// <param name="Done"><see langword="false"/> while the server is still processing.</param>
/// <param name="Error">The failure, if the operation failed.</param>
internal sealed record OperationDto(string? Name, bool Done, StatusDto? Error);

/// <summary>
/// A Google API error status.
/// </summary>
/// <param name="Code">HTTP-like status code.</param>
/// <param name="Message">Developer-facing message.</param>
/// <param name="Status">Canonical status name, for example <c>FAILED_PRECONDITION</c>.</param>
internal sealed record StatusDto(int Code, string? Message, string? Status);

/// <summary>
/// The body of a failed Google API request.
/// </summary>
/// <param name="Error">The error.</param>
internal sealed record ErrorResponseDto(StatusDto? Error);
