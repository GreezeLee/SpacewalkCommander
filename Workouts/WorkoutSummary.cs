namespace UrevoSpacewalkCommander.Workouts;

/// <summary>
/// Key figures of a finished workout, derived from its raw recording by <see cref="WorkoutAnalyzer"/>.
/// </summary>
/// <param name="ActiveDuration">Time the belt ran, excluding pauses.</param>
/// <param name="DistanceMeters">Distance walked in metres.</param>
/// <param name="EnergyKcal">Active energy in kilocalories; <see langword="null"/> if neither reported nor estimable.</param>
/// <param name="EnergyEstimated">
/// <see langword="true"/> if <paramref name="EnergyKcal"/> was estimated from speed, incline and body weight
/// because the treadmill does not report energy.
/// </param>
/// <param name="AverageSpeedKmh">Distance divided by active duration, in km/h.</param>
/// <param name="MaxSpeedKmh">Highest reported belt speed in km/h.</param>
/// <param name="AverageInclinePercent">Distance-weighted mean incline in percent.</param>
/// <param name="MaxInclinePercent">Highest reported incline in percent.</param>
/// <param name="ElevationGainMeters">Virtual climb: distance times incline, summed up, in metres.</param>
/// <param name="AverageHeartRate">Mean heart rate; <see langword="null"/> if the treadmill measured none.</param>
/// <param name="Splits">One entry per full kilometre plus the remaining part.</param>
internal sealed record WorkoutSummary(
  TimeSpan ActiveDuration,
  double DistanceMeters,
  double? EnergyKcal,
  bool EnergyEstimated,
  double AverageSpeedKmh,
  double MaxSpeedKmh,
  double AverageInclinePercent,
  double MaxInclinePercent,
  double ElevationGainMeters,
  int? AverageHeartRate,
  IReadOnlyList<WorkoutSplit> Splits);

/// <summary>
/// A section of a workout, normally one kilometre.
/// </summary>
/// <param name="StartTime">When the section began.</param>
/// <param name="EndTime">When the section ended.</param>
/// <param name="DistanceMeters">Distance of the section in metres.</param>
/// <param name="EnergyKcal">Active energy of the section; <see langword="null"/> if unknown.</param>
internal sealed record WorkoutSplit(
  DateTimeOffset StartTime,
  DateTimeOffset EndTime,
  double DistanceMeters,
  double? EnergyKcal);
