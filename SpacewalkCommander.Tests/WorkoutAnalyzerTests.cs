using SpacewalkCommander.Workouts;

namespace SpacewalkCommander.Tests;

/// <summary>
/// Tests the derivation of workout key figures from raw recordings.
/// </summary>
public sealed class WorkoutAnalyzerTests
{
  /// <summary>Start time of the test workouts (UTC+1, like Swiss winter time).</summary>
  private static readonly DateTimeOffset s_start = new(2026, 11, 6, 18, 0, 0, TimeSpan.FromHours(1));

  /// <summary>Distance comes from the treadmill counter, and a counter reset is not counted as negative progress.</summary>
  [Fact]
  public void Analyze_UsesDistanceCounterAndHandlesReset()
  {
    WorkoutSample[] samples =
    [
      Sample(0, distance: 100),
      Sample(1, distance: 150),
      Sample(2, distance: 5),
      Sample(3, distance: 25),
    ];

    WorkoutSummary summary = WorkoutAnalyzer.Analyze(Session(samples, 3), bodyWeightKg: null);

    Assert.Equal(50 + 5 + 20, summary.DistanceMeters, precision: 6);
  }

  /// <summary>Without a counter, distance is integrated from the speed (3.6 km/h = 1 m/s).</summary>
  [Fact]
  public void Analyze_IntegratesSpeedWithoutCounter()
  {
    WorkoutSample[] samples = [.. Enumerable.Range(0, 101).Select(second => Sample(second, speed: 3.6))];

    WorkoutSummary summary = WorkoutAnalyzer.Analyze(Session(samples, 100), bodyWeightKg: null);

    Assert.Equal(100, summary.DistanceMeters, precision: 6);
    Assert.Equal(3.6, summary.AverageSpeedKmh, precision: 6);
  }

  /// <summary>A Bluetooth dropout of more than 5 s does not add integrated distance.</summary>
  [Fact]
  public void Analyze_DoesNotIntegrateAcrossLongGaps()
  {
    WorkoutSample[] samples = [Sample(0, speed: 3.6), Sample(1, speed: 3.6), Sample(60, speed: 3.6)];

    WorkoutSummary summary = WorkoutAnalyzer.Analyze(Session(samples, 60), bodyWeightKg: null);

    Assert.Equal(1, summary.DistanceMeters, precision: 6);
  }

  /// <summary>Incline turns distance into virtual climb, and every full kilometre becomes a split.</summary>
  [Fact]
  public void Analyze_ComputesElevationAndSplits()
  {
    WorkoutSample[] samples =
    [
      .. Enumerable.Range(0, 251).Select(second => Sample(second, speed: 36, incline: 5, distance: second * 10)),
    ];

    WorkoutSummary summary = WorkoutAnalyzer.Analyze(Session(samples, 250), bodyWeightKg: null);

    Assert.Equal(2500, summary.DistanceMeters, precision: 6);
    Assert.Equal(125, summary.ElevationGainMeters, precision: 6);
    Assert.Equal(5, summary.AverageInclinePercent, precision: 6);
    Assert.Equal([1000d, 1000d, 500d], summary.Splits.Select(split => split.DistanceMeters));
    Assert.Equal(s_start.AddSeconds(100), summary.Splits[0].EndTime);
    Assert.Equal(s_start.AddSeconds(200), summary.Splits[1].EndTime);
    Assert.Equal(s_start.AddSeconds(250), summary.Splits[2].EndTime);
  }

  /// <summary>Energy is estimated with the ACSM equation when a body weight is configured.</summary>
  [Fact]
  public void Analyze_EstimatesEnergyWithBodyWeight()
  {
    WorkoutSample[] samples = [.. Enumerable.Range(0, 61).Select(second => Sample(second, speed: 6))];

    WorkoutSummary withWeight = WorkoutAnalyzer.Analyze(Session(samples, 60), bodyWeightKg: 80);
    WorkoutSummary withoutWeight = WorkoutAnalyzer.Analyze(Session(samples, 60), bodyWeightKg: null);

    // 100 m/min * 0.1 ml/kg/m = 10 ml/kg/min; 10 * 80 kg / 1000 * 5 kcal/l = 4 kcal per minute.
    Assert.Equal(4, withWeight.EnergyKcal!.Value, precision: 6);
    Assert.True(withWeight.EnergyEstimated);
    Assert.Null(withoutWeight.EnergyKcal);
  }

  /// <summary>Reported energy wins over the estimate.</summary>
  [Fact]
  public void Analyze_PrefersReportedEnergy()
  {
    WorkoutSample[] samples = [Sample(0, speed: 6, energy: 10), Sample(30, speed: 6, energy: 14)];

    WorkoutSummary summary = WorkoutAnalyzer.Analyze(Session(samples, 30), bodyWeightKg: 80);

    Assert.Equal(4, summary.EnergyKcal!.Value, precision: 6);
    Assert.False(summary.EnergyEstimated);
  }

  /// <summary>Pauses do not count as active time.</summary>
  [Fact]
  public void ComputeActiveDuration_ExcludesPauses()
  {
    var session = new WorkoutSession(
      Guid.NewGuid(),
      "URTM",
      s_start,
      s_start.AddMinutes(30),
      [
        new WorkoutEvent(s_start, WorkoutEventKind.Start),
        new WorkoutEvent(s_start.AddMinutes(10), WorkoutEventKind.Pause),
        new WorkoutEvent(s_start.AddMinutes(15), WorkoutEventKind.Resume),
        new WorkoutEvent(s_start.AddMinutes(30), WorkoutEventKind.Stop),
      ],
      []);

    Assert.Equal(TimeSpan.FromMinutes(25), WorkoutAnalyzer.ComputeActiveDuration(session));
  }

  /// <summary>Accidental short starts are recognised.</summary>
  [Fact]
  public void IsTooShort_DetectsAccidentalStart()
  {
    WorkoutSample[] samples = [.. Enumerable.Range(0, 31).Select(second => Sample(second, speed: 3.6))];

    Assert.True(WorkoutAnalyzer.IsTooShort(WorkoutAnalyzer.Analyze(Session(samples, 30), null)));
  }

  /// <summary>
  /// Creates a sample at a time offset from the workout start.
  /// </summary>
  /// <param name="second">Seconds after the start.</param>
  /// <param name="speed">Speed in km/h.</param>
  /// <param name="incline">Incline in percent.</param>
  /// <param name="distance">Distance counter in metres.</param>
  /// <param name="energy">Energy counter in kcal.</param>
  /// <returns>The sample.</returns>
  private static WorkoutSample Sample(
    int second,
    double? speed = null,
    double? incline = null,
    int? distance = null,
    int? energy = null)
  {
    return new WorkoutSample(s_start.AddSeconds(second), speed, incline, distance, energy, null);
  }

  /// <summary>
  /// Creates a workout without pauses.
  /// </summary>
  /// <param name="samples">The samples.</param>
  /// <param name="durationSeconds">Duration of the workout.</param>
  /// <returns>The workout.</returns>
  private static WorkoutSession Session(IReadOnlyList<WorkoutSample> samples, int durationSeconds)
  {
    DateTimeOffset end = s_start.AddSeconds(durationSeconds);
    return new WorkoutSession(
      Guid.NewGuid(),
      "URTM",
      s_start,
      end,
      [new WorkoutEvent(s_start, WorkoutEventKind.Start), new WorkoutEvent(end, WorkoutEventKind.Stop)],
      samples);
  }
}
