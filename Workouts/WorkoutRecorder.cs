using UrevoSpacewalkCommander.Bluetooth;

namespace UrevoSpacewalkCommander.Workouts;

/// <summary>
/// Records the workout that is in progress. All methods are idempotent with respect to the belt state, because
/// the same transition is usually reported twice: once by the confirmed command and once by the treadmill's
/// status notification.
/// </summary>
/// <remarks>Not thread-safe; used from the UI thread only.</remarks>
internal sealed class WorkoutRecorder
{
  /// <summary>Events of the current workout.</summary>
  private readonly List<WorkoutEvent> _events = [];

  /// <summary>Samples of the current workout.</summary>
  private readonly List<WorkoutSample> _samples = [];

  /// <summary>Start of the current workout; <see langword="null"/> if no workout is in progress.</summary>
  private DateTimeOffset? _startTime;

  /// <summary>Bluetooth name of the treadmill of the current workout.</summary>
  private string _deviceName = string.Empty;

  /// <summary>Whether the belt currently runs; samples are only recorded while it does.</summary>
  private bool _isRunning;

  /// <summary>Gets a value indicating whether a workout is in progress (running or paused).</summary>
  public bool IsActive => _startTime is not null;

  /// <summary>
  /// Begins a new workout, or resumes the current one after a pause.
  /// </summary>
  /// <param name="time">When the belt started.</param>
  /// <param name="deviceName">Bluetooth name of the treadmill, stored with a new workout.</param>
  public void StartOrResume(DateTimeOffset time, string deviceName)
  {
    if (_startTime is null)
    {
      _startTime = time;
      _deviceName = deviceName;
      _events.Add(new WorkoutEvent(time, WorkoutEventKind.Start));
    }
    else if (!_isRunning)
    {
      _events.Add(new WorkoutEvent(time, WorkoutEventKind.Resume));
    }

    _isRunning = true;
  }

  /// <summary>
  /// Marks a pause of the running workout.
  /// </summary>
  /// <param name="time">When the belt paused.</param>
  public void Pause(DateTimeOffset time)
  {
    if (IsActive && _isRunning)
    {
      _events.Add(new WorkoutEvent(time, WorkoutEventKind.Pause));
      _isRunning = false;
    }
  }

  /// <summary>
  /// Records a telemetry notification while the belt runs.
  /// </summary>
  /// <param name="data">The decoded telemetry.</param>
  /// <param name="time">When it was received.</param>
  public void AddSample(TreadmillData data, DateTimeOffset time)
  {
    if (IsActive && _isRunning)
    {
      _samples.Add(new WorkoutSample(
        time,
        data.SpeedKmh,
        data.InclinePercent,
        data.DistanceMeters,
        data.EnergyKcal,
        data.HeartRate));
    }
  }

  /// <summary>
  /// Ends the workout and returns its recording.
  /// </summary>
  /// <param name="time">When the workout ended.</param>
  /// <returns>The finished workout, or <see langword="null"/> if none was in progress.</returns>
  public WorkoutSession? Finish(DateTimeOffset time)
  {
    if (_startTime is not { } startTime)
    {
      return null;
    }

    _events.Add(new WorkoutEvent(time, WorkoutEventKind.Stop));
    var session = new WorkoutSession(Guid.NewGuid(), _deviceName, startTime, time, [.. _events], [.. _samples]);
    _events.Clear();
    _samples.Clear();
    _startTime = null;
    _isRunning = false;
    return session;
  }
}
