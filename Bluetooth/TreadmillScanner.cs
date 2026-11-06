using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;

namespace SpacewalkCommander.Bluetooth;

/// <summary>
/// Scans for nearby Bluetooth LE devices. Events are raised on background threads.
/// </summary>
internal sealed class TreadmillScanner
{
  /// <summary>The underlying Windows advertisement watcher.</summary>
  private readonly BluetoothLEAdvertisementWatcher _watcher;

  /// <summary>
  /// Initializes a scanner that uses active scanning, so that devices also send their name in the scan response.
  /// </summary>
  public TreadmillScanner()
  {
    _watcher = new BluetoothLEAdvertisementWatcher
    {
      ScanningMode = BluetoothLEScanningMode.Active,
    };
    _watcher.Received += OnReceived;
    _watcher.Stopped += OnStopped;
  }

  /// <summary>Raised for every received advertisement, with the device it came from.</summary>
  public event Action<BleDeviceInfo>? DeviceFound;

  /// <summary>Raised when the scan ended on its own because of an error (for example Bluetooth is switched off).</summary>
  public event Action<BluetoothError>? ScanFailed;

  /// <summary>
  /// Starts scanning. Has no effect if a scan is already running.
  /// </summary>
  public void Start()
  {
    if (_watcher.Status is BluetoothLEAdvertisementWatcherStatus.Created
      or BluetoothLEAdvertisementWatcherStatus.Stopped
      or BluetoothLEAdvertisementWatcherStatus.Aborted)
    {
      _watcher.Start();
    }
  }

  /// <summary>
  /// Stops scanning. Has no effect if no scan is running.
  /// </summary>
  public void Stop()
  {
    if (_watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started)
    {
      _watcher.Stop();
    }
  }

  /// <summary>
  /// Converts a received advertisement into a <see cref="BleDeviceInfo"/>.
  /// </summary>
  /// <param name="sender">The watcher.</param>
  /// <param name="args">The advertisement.</param>
  private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
  {
    bool advertisesFtms = args.Advertisement.ServiceUuids.Contains(FtmsProtocol.FitnessMachineService);
    DeviceFound?.Invoke(new BleDeviceInfo(
      args.BluetoothAddress,
      args.Advertisement.LocalName,
      advertisesFtms,
      args.RawSignalStrengthInDBm));
  }

  /// <summary>
  /// Checks that the PC has a Bluetooth radio that is switched on and that the app may use it.
  /// </summary>
  /// <returns>A description of the problem, or <see langword="null"/> if the radio is ready.</returns>
  public static async Task<string?> CheckRadioAsync()
  {
    RadioAccessStatus access = await Radio.RequestAccessAsync();
    if (access != RadioAccessStatus.Allowed)
    {
      return $"Access to Bluetooth is not allowed ({access}). Check Windows Settings > Privacy.";
    }

    IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
    Radio? bluetooth = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
    if (bluetooth is null)
    {
      return "This PC has no Bluetooth adapter.";
    }

    return bluetooth.State == RadioState.On ? null : $"Bluetooth is not switched on ({bluetooth.State}).";
  }

  /// <summary>
  /// Reports a scan that was aborted by the system; a normal <see cref="Stop"/> reports nothing.
  /// </summary>
  /// <param name="sender">The watcher.</param>
  /// <param name="args">The reason the watcher stopped.</param>
  private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
  {
    if (args.Error != BluetoothError.Success)
    {
      ScanFailed?.Invoke(args.Error);
    }
  }
}
