namespace UrevoSpacewalkCommander.Bluetooth;

/// <summary>
/// One decoded FTMS "Treadmill Data" notification. A value is <see langword="null"/> when the treadmill
/// did not include the corresponding field in the notification.
/// </summary>
/// <param name="SpeedKmh">Instantaneous belt speed in km/h.</param>
/// <param name="InclinePercent">Current incline in percent.</param>
/// <param name="DistanceMeters">Total distance since the start of the session in metres.</param>
/// <param name="ElapsedSeconds">Elapsed session time in seconds.</param>
/// <param name="EnergyKcal">Total expended energy in kilocalories.</param>
/// <param name="HeartRate">Heart rate in beats per minute.</param>
internal sealed record TreadmillData(
  double? SpeedKmh,
  double? InclinePercent,
  int? DistanceMeters,
  int? ElapsedSeconds,
  int? EnergyKcal,
  int? HeartRate);
