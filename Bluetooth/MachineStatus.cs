namespace SpacewalkCommander.Bluetooth;

/// <summary>
/// Kind of event reported by the FTMS "Fitness Machine Status" characteristic.
/// </summary>
internal enum MachineStatusKind
{
  /// <summary>An event this application does not interpret.</summary>
  Unknown,

  /// <summary>The treadmill was reset.</summary>
  Reset,

  /// <summary>The belt was stopped (by the user, the console or this application).</summary>
  Stopped,

  /// <summary>The belt was paused.</summary>
  Paused,

  /// <summary>The belt was stopped by the safety key.</summary>
  StoppedBySafetyKey,

  /// <summary>The belt was started or resumed.</summary>
  Started,

  /// <summary>The target speed changed.</summary>
  TargetSpeedChanged,

  /// <summary>The target incline changed.</summary>
  TargetInclineChanged,

  /// <summary>Another client (for example the Urevo app) took over control of the treadmill.</summary>
  ControlPermissionLost,
}
