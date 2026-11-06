namespace UrevoSpacewalkCommander.Workouts;

/// <summary>
/// Derives the key figures of a workout (distance, energy, speeds, incline, virtual climb, splits) from its raw
/// recording.
/// </summary>
/// <remarks>
/// Distance and energy come from the treadmill's counters when it reports them, because those are what the
/// treadmill console shows. Without counters, distance is integrated from the speed and energy is estimated with
/// the ACSM walking equation, if a body weight is configured.
/// </remarks>
internal static class WorkoutAnalyzer
{
  /// <summary>Length of one split; Google Health's default split is 1 km.</summary>
  private const double SplitDistanceMeters = 1000;

  /// <summary>A last partial split shorter than this is dropped as noise.</summary>
  private const double MinRemainderSplitMeters = 10;

  /// <summary>
  /// Longer gaps between two samples (Bluetooth dropouts) are not integrated, so a lost connection does not add
  /// phantom distance.
  /// </summary>
  private const double MaxIntegrationGapSeconds = 5;

  /// <summary>Workouts with less active time are not kept (accidental start).</summary>
  private const double MinimumActiveSeconds = 60;

  /// <summary>Workouts with less distance are not kept (accidental start).</summary>
  private const double MinimumDistanceMeters = 20;

  /// <summary>ACSM walking equation: oxygen cost of horizontal walking in ml O2 per kg and metre.</summary>
  private const double AcsmHorizontalCoefficient = 0.1;

  /// <summary>ACSM walking equation: oxygen cost of vertical work in ml O2 per kg and metre of climb.</summary>
  private const double AcsmVerticalCoefficient = 1.8;

  /// <summary>Energy per litre of oxygen consumed, in kilocalories.</summary>
  private const double KcalPerLiterOxygen = 5.0;

  /// <summary>
  /// Computes the key figures of a workout.
  /// </summary>
  /// <param name="session">The recorded workout.</param>
  /// <param name="bodyWeightKg">
  /// Body weight for the energy estimate; <see langword="null"/> disables the estimate.
  /// </param>
  /// <returns>The summary.</returns>
  public static WorkoutSummary Analyze(WorkoutSession session, double? bodyWeightKg)
  {
    var track = new Track(session, bodyWeightKg);
    foreach (WorkoutSample sample in session.Samples.OrderBy(sample => sample.Time))
    {
      track.Add(sample);
    }

    track.CloseLastSplit(session.EndTime);
    TimeSpan activeDuration = ComputeActiveDuration(session);
    double averageSpeed = activeDuration.TotalSeconds > 0 ? track.Distance / activeDuration.TotalSeconds * 3.6 : 0;
    return new WorkoutSummary(
      activeDuration,
      track.Distance,
      track.Energy,
      track.EnergyEstimated,
      averageSpeed,
      track.MaxSpeed,
      track.AverageIncline,
      track.MaxIncline,
      track.ElevationGain,
      track.AverageHeartRate,
      track.Splits);
  }

  /// <summary>
  /// Tells whether a workout is too short to be worth keeping, typically an accidental start.
  /// </summary>
  /// <param name="summary">The workout's summary.</param>
  /// <returns><see langword="true"/> if the workout should be discarded.</returns>
  public static bool IsTooShort(WorkoutSummary summary)
  {
    return summary.ActiveDuration.TotalSeconds < MinimumActiveSeconds
      || summary.DistanceMeters < MinimumDistanceMeters;
  }

  /// <summary>
  /// Sums up the time between start/resume and pause/stop events.
  /// </summary>
  /// <param name="session">The recorded workout.</param>
  /// <returns>The active time; the whole session if the events give none.</returns>
  public static TimeSpan ComputeActiveDuration(WorkoutSession session)
  {
    TimeSpan total = TimeSpan.Zero;
    DateTimeOffset? runningSince = null;
    foreach (WorkoutEvent workoutEvent in session.Events.OrderBy(workoutEvent => workoutEvent.Time))
    {
      if (workoutEvent.Kind is WorkoutEventKind.Start or WorkoutEventKind.Resume)
      {
        runningSince ??= workoutEvent.Time;
      }
      else if (runningSince is { } since)
      {
        total += workoutEvent.Time - since;
        runningSince = null;
      }
    }

    return total > TimeSpan.Zero ? total : session.EndTime - session.StartTime;
  }

  /// <summary>
  /// Estimates the active (non-basal) energy of walking with the ACSM walking equation.
  /// </summary>
  /// <param name="speedKmh">Belt speed in km/h.</param>
  /// <param name="inclinePercent">Incline in percent; negative values count as level.</param>
  /// <param name="bodyWeightKg">Body weight in kg.</param>
  /// <param name="seconds">Duration in seconds.</param>
  /// <returns>The energy in kilocalories.</returns>
  public static double EstimateActiveKcal(double speedKmh, double inclinePercent, double bodyWeightKg, double seconds)
  {
    double metersPerMinute = speedKmh * 1000 / 60;
    double grade = Math.Max(inclinePercent, 0) / 100;
    double oxygenMlPerKgMinute = AcsmHorizontalCoefficient * metersPerMinute
      + AcsmVerticalCoefficient * metersPerMinute * grade;
    return oxygenMlPerKgMinute * bodyWeightKg / 1000 * KcalPerLiterOxygen * seconds / 60;
  }

  /// <summary>
  /// Accumulates the figures while walking through the samples in chronological order.
  /// </summary>
  private sealed class Track
  {
    /// <summary>Whether distance comes from the treadmill's counter instead of integrated speed.</summary>
    private readonly bool _reportsDistance;

    /// <summary>Whether energy comes from the treadmill's counter.</summary>
    private readonly bool _reportsEnergy;

    /// <summary>Body weight for the energy estimate; <see langword="null"/> if energy is reported or not estimable.</summary>
    private readonly double? _estimationWeightKg;

    /// <summary>Splits completed so far.</summary>
    private readonly List<WorkoutSplit> _splits = [];

    /// <summary>Time of the previous sample.</summary>
    private DateTimeOffset? _lastTime;

    /// <summary>Last known belt speed in km/h.</summary>
    private double _speed;

    /// <summary>Last known incline in percent.</summary>
    private double _incline;

    /// <summary>Last value of the treadmill's distance counter.</summary>
    private int? _distanceCounter;

    /// <summary>Last value of the treadmill's energy counter.</summary>
    private int? _energyCounter;

    /// <summary>Energy accumulated so far in kcal.</summary>
    private double _energy;

    /// <summary>Sum of incline times distance, for the distance-weighted mean incline.</summary>
    private double _inclineDistanceSum;

    /// <summary>Sum of all heart rate values.</summary>
    private double _heartRateSum;

    /// <summary>Number of heart rate values.</summary>
    private int _heartRateCount;

    /// <summary>Start time of the split in progress.</summary>
    private DateTimeOffset _splitStartTime;

    /// <summary>Accumulated energy at the start of the split in progress.</summary>
    private double _splitStartEnergy;

    /// <summary>Total distance at which the split in progress ends.</summary>
    private double _nextSplitBoundary = SplitDistanceMeters;

    /// <summary>
    /// Prepares the accumulation for a workout.
    /// </summary>
    /// <param name="session">The workout.</param>
    /// <param name="bodyWeightKg">Body weight for the energy estimate, if configured.</param>
    public Track(WorkoutSession session, double? bodyWeightKg)
    {
      _reportsDistance = session.Samples.Any(sample => sample.DistanceMeters is not null);
      _reportsEnergy = session.Samples.Any(sample => sample.EnergyKcal is not null);
      _estimationWeightKg = !_reportsEnergy && bodyWeightKg is > 0 ? bodyWeightKg : null;
      _splitStartTime = session.StartTime;
    }

    /// <summary>Gets the distance in metres.</summary>
    public double Distance { get; private set; }

    /// <summary>Gets the virtual climb in metres.</summary>
    public double ElevationGain { get; private set; }

    /// <summary>Gets the highest speed in km/h.</summary>
    public double MaxSpeed { get; private set; }

    /// <summary>Gets the highest incline in percent.</summary>
    public double MaxIncline { get; private set; }

    /// <summary>Gets the energy in kcal, or <see langword="null"/> if neither reported nor estimable.</summary>
    public double? Energy => _reportsEnergy || EnergyEstimated ? _energy : null;

    /// <summary>Gets a value indicating whether the energy is an estimate.</summary>
    public bool EnergyEstimated => _estimationWeightKg is not null;

    /// <summary>Gets the distance-weighted mean incline in percent.</summary>
    public double AverageIncline => Distance > 0 ? _inclineDistanceSum / Distance : 0;

    /// <summary>Gets the mean heart rate, or <see langword="null"/> without heart rate values.</summary>
    public int? AverageHeartRate => _heartRateCount > 0 ? (int)Math.Round(_heartRateSum / _heartRateCount) : null;

    /// <summary>Gets the splits completed so far.</summary>
    public IReadOnlyList<WorkoutSplit> Splits => _splits;

    /// <summary>
    /// Adds the progress up to <paramref name="sample"/>, then takes over its values.
    /// </summary>
    /// <param name="sample">The next sample in chronological order.</param>
    public void Add(WorkoutSample sample)
    {
      double seconds = _lastTime is { } last ? Math.Max((sample.Time - last).TotalSeconds, 0) : 0;
      double integrableSeconds = seconds <= MaxIntegrationGapSeconds ? seconds : 0;
      double distanceDelta = _reportsDistance
        ? CounterDelta(ref _distanceCounter, sample.DistanceMeters)
        : _speed / 3.6 * integrableSeconds;
      double energyDelta = _reportsEnergy
        ? CounterDelta(ref _energyCounter, sample.EnergyKcal)
        : _estimationWeightKg is { } weight ? EstimateActiveKcal(_speed, _incline, weight, integrableSeconds) : 0;

      AddProgress(sample.Time, seconds, distanceDelta, energyDelta);
      TakeOver(sample);
    }

    /// <summary>
    /// Adds the remaining distance after the last full kilometre as a final split.
    /// </summary>
    /// <param name="endTime">End of the workout.</param>
    public void CloseLastSplit(DateTimeOffset endTime)
    {
      double remainder = Distance - (_nextSplitBoundary - SplitDistanceMeters);
      if (remainder >= MinRemainderSplitMeters)
      {
        _splits.Add(new WorkoutSplit(_splitStartTime, endTime, remainder, SplitEnergy(_energy)));
      }
    }

    /// <summary>
    /// Returns how far a counter advanced and remembers its new value. A counter that went backwards was reset
    /// by the treadmill; then its new value is the progress since the reset.
    /// </summary>
    /// <param name="counter">The last value; updated.</param>
    /// <param name="value">The new value, or <see langword="null"/> if this sample does not contain it.</param>
    /// <returns>The progress; zero for the first value, which only serves as the baseline.</returns>
    private static double CounterDelta(ref int? counter, int? value)
    {
      if (value is not { } current)
      {
        return 0;
      }

      int delta = counter is { } previous ? current - previous : 0;
      counter = current;
      return delta < 0 ? current : delta;
    }

    /// <summary>
    /// Adds distance and energy of one step and completes the splits it crosses.
    /// </summary>
    /// <param name="time">End of the step.</param>
    /// <param name="seconds">Length of the step.</param>
    /// <param name="distanceDelta">Distance of the step in metres.</param>
    /// <param name="energyDelta">Energy of the step in kcal.</param>
    private void AddProgress(DateTimeOffset time, double seconds, double distanceDelta, double energyDelta)
    {
      double distanceBefore = Distance;
      double energyBefore = _energy;
      Distance += distanceDelta;
      _energy += energyDelta;
      _inclineDistanceSum += _incline * distanceDelta;
      ElevationGain += distanceDelta * Math.Max(_incline, 0) / 100;

      while (distanceDelta > 0 && Distance >= _nextSplitBoundary)
      {
        double fraction = (_nextSplitBoundary - distanceBefore) / distanceDelta;
        DateTimeOffset boundaryTime = time - TimeSpan.FromSeconds(seconds * (1 - fraction));
        double boundaryEnergy = energyBefore + energyDelta * fraction;
        _splits.Add(new WorkoutSplit(_splitStartTime, boundaryTime, SplitDistanceMeters, SplitEnergy(boundaryEnergy)));
        _splitStartTime = boundaryTime;
        _splitStartEnergy = boundaryEnergy;
        _nextSplitBoundary += SplitDistanceMeters;
      }
    }

    /// <summary>
    /// Computes the energy of the split in progress up to a given accumulated energy.
    /// </summary>
    /// <param name="energyAtEnd">Accumulated energy at the end of the split.</param>
    /// <returns>The split's energy, or <see langword="null"/> if energy is unknown.</returns>
    private double? SplitEnergy(double energyAtEnd)
    {
      return Energy is null ? null : energyAtEnd - _splitStartEnergy;
    }

    /// <summary>
    /// Takes over speed, incline and heart rate of a sample for the following steps.
    /// </summary>
    /// <param name="sample">The sample.</param>
    private void TakeOver(WorkoutSample sample)
    {
      if (_lastTime is null || sample.Time > _lastTime)
      {
        _lastTime = sample.Time;
      }

      if (sample.SpeedKmh is { } speed)
      {
        _speed = speed;
        MaxSpeed = Math.Max(MaxSpeed, speed);
      }

      if (sample.InclinePercent is { } incline)
      {
        _incline = incline;
        MaxIncline = Math.Max(MaxIncline, incline);
      }

      if (sample.HeartRate is { } heartRate)
      {
        _heartRateSum += heartRate;
        _heartRateCount++;
      }
    }
  }
}
