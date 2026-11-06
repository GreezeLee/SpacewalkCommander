namespace UrevoSpacewalkCommander.Bluetooth;

/// <summary>
/// A closed numeric range with a step size, as reported by the treadmill (speed in km/h, incline in percent).
/// </summary>
/// <param name="Min">Smallest value the treadmill accepts.</param>
/// <param name="Max">Largest value the treadmill accepts.</param>
/// <param name="Step">Smallest increment between two accepted values.</param>
internal sealed record ValueRange(double Min, double Max, double Step);
