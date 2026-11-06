namespace SpacewalkCommander.Workouts;

/// <summary>
/// One telemetry notification of the treadmill, stamped with the time it was received. Fields are
/// <see langword="null"/> when the notification did not contain them; FTMS treadmills may spread the fields over
/// several notifications.
/// </summary>
/// <param name="Time">Local receive time.</param>
/// <param name="SpeedKmh">Belt speed in km/h.</param>
/// <param name="InclinePercent">Incline in percent.</param>
/// <param name="DistanceMeters">Distance counter of the treadmill in metres.</param>
/// <param name="EnergyKcal">Energy counter of the treadmill in kilocalories.</param>
/// <param name="HeartRate">Heart rate in beats per minute.</param>
internal sealed record WorkoutSample(
  DateTimeOffset Time,
  double? SpeedKmh,
  double? InclinePercent,
  int? DistanceMeters,
  int? EnergyKcal,
  int? HeartRate);
