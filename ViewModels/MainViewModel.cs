using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UrevoSpacewalkCommander.Bluetooth;
using UrevoSpacewalkCommander.Infrastructure;
using UrevoSpacewalkCommander.Workouts;

namespace UrevoSpacewalkCommander.ViewModels;

/// <summary>
/// State and commands of the main window: finding and connecting the treadmill, starting and stopping the belt,
/// and setting speed and incline.
/// </summary>
internal sealed class MainViewModel : ObservableObject
{
  /// <summary>Speed range used until the treadmill reports its own, in km/h.</summary>
  private const double DefaultMinSpeedKmh = 1.0;

  /// <summary>Speed range used until the treadmill reports its own, in km/h.</summary>
  private const double DefaultMaxSpeedKmh = 6.0;

  /// <summary>Speed selection step in km/h, used if the treadmill reports none.</summary>
  private const double DefaultSpeedStepKmh = 0.1;

  /// <summary>Incline range used until the treadmill reports its own, in percent.</summary>
  private const double DefaultMaxInclinePercent = 15.0;

  /// <summary>Incline selection step in percent, used if the treadmill reports none.</summary>
  private const double DefaultInclineStepPercent = 1.0;

  /// <summary>Speed that is preselected before the first start, in km/h.</summary>
  private const double InitialSpeedKmh = 2.0;

  /// <summary>Maximum number of lines kept in the log.</summary>
  private const int MaxLogEntries = 300;

  /// <summary>Text shown for a value the treadmill does not report.</summary>
  private const string NoValueText = "-";

  /// <summary>
  /// Name fragments that identify a Urevo treadmill (compared case-insensitively): the first match is preselected
  /// and connected automatically at start-up. The Spacewalk 3S advertises itself as "URTM...".
  /// </summary>
  private static readonly string[] s_preferredNameParts = ["urtm", "urevo", "spacewalk"];

  /// <summary>How long a scan runs before it stops on its own.</summary>
  private static readonly TimeSpan s_scanDuration = TimeSpan.FromSeconds(20);

  /// <summary>Pause after the last slider movement before the new value is sent.</summary>
  private static readonly TimeSpan s_settingDebounce = TimeSpan.FromMilliseconds(400);

  /// <summary>Time the belt gets after a start before the target speed is sent.</summary>
  private static readonly TimeSpan s_startSettleDelay = TimeSpan.FromSeconds(1);

  /// <summary>How long closing the application waits for the upload of the last workout.</summary>
  private static readonly TimeSpan s_exitUploadTimeout = TimeSpan.FromSeconds(30);

  /// <summary>Time to wait before the second attempt to set the speed after a start (start countdown).</summary>
  private static readonly TimeSpan s_startRetryDelay = TimeSpan.FromSeconds(3);

  /// <summary>Dispatcher of the UI thread; Bluetooth events arrive on other threads.</summary>
  private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

  /// <summary>Scans for treadmills.</summary>
  private readonly TreadmillScanner _scanner = new();

  /// <summary>Delays sending the speed while the slider is still moving.</summary>
  private readonly Debouncer _speedDebouncer = new(s_settingDebounce);

  /// <summary>Delays sending the incline while the slider is still moving.</summary>
  private readonly Debouncer _inclineDebouncer = new(s_settingDebounce);

  /// <summary>Cancelled when the application shuts down; ends all pending Bluetooth operations.</summary>
  private readonly CancellationTokenSource _lifetime = new();

  /// <summary>Strongest signal seen per device address during the current scan.</summary>
  private readonly Dictionary<ulong, short> _bestRssi = [];

  /// <summary>Number of advertisements received during the current scan.</summary>
  private int _advertisementCount;

  /// <summary>
  /// <see langword="true"/> while the start-up scan waits for a Urevo / Spacewalk to connect to; reset when the
  /// connection starts or the scan ends.
  /// </summary>
  private bool _autoConnectPending;

  /// <summary>Cancels the running scan; <see langword="null"/> if none runs.</summary>
  private CancellationTokenSource? _scanCancellation;

  /// <summary>The connected treadmill; <see langword="null"/> if not connected.</summary>
  private FtmsTreadmillClient? _client;

  /// <summary>Records the workout in progress.</summary>
  private readonly WorkoutRecorder _recorder = new();

  /// <summary>Bluetooth name of the connected treadmill, stored with each workout.</summary>
  private string _connectedDeviceName = string.Empty;

  /// <summary>The upload started for the last finished workout; awaited (bounded) when the application closes.</summary>
  private Task _uploadTask = Task.CompletedTask;

  private BleDeviceInfo? _selectedDevice;
  private bool _isScanning;
  private bool _isConnecting;
  private bool _isConnected;
  private bool _isRunning;
  private string _statusText = "Not connected";
  private double _minSpeed = DefaultMinSpeedKmh;
  private double _maxSpeed = DefaultMaxSpeedKmh;
  private double _speedStep = DefaultSpeedStepKmh;
  private double _targetSpeed = InitialSpeedKmh;
  private double _minIncline;
  private double _maxIncline = DefaultMaxInclinePercent;
  private double _inclineStep = DefaultInclineStepPercent;
  private double _targetIncline;
  private string _currentSpeedText = NoValueText;
  private string _currentInclineText = NoValueText;
  private string _distanceText = NoValueText;
  private string _elapsedText = NoValueText;
  private string _energyText = NoValueText;
  private string _heartRateText = NoValueText;

  /// <summary>
  /// Creates the view model and its commands.
  /// </summary>
  public MainViewModel()
  {
    WorkoutSync = new WorkoutSyncViewModel(message => PostToUi(() => AddLog(message)));
    _scanner.DeviceFound += device => PostToUi(() => OnDeviceFound(device));
    _scanner.ScanFailed += error => PostToUi(() => OnScanFailed(error.ToString()));

    ScanCommand = new RelayCommand(ScanAsync, () => !IsScanning && !IsConnected && !IsConnecting);
    ConnectCommand = new RelayCommand(ConnectAsync, () => SelectedDevice is not null && !IsConnected && !IsConnecting);
    DisconnectCommand = new RelayCommand(DisconnectAsync, () => IsConnected);
    StartCommand = new RelayCommand(StartAsync, () => IsConnected && !IsRunning);
    PauseCommand = new RelayCommand(PauseAsync, () => IsConnected && IsRunning);
    StopCommand = new RelayCommand(StopAsync, () => IsConnected);
    SpeedUpCommand = new RelayCommand(() => TargetSpeed += SpeedStep, () => IsConnected);
    SpeedDownCommand = new RelayCommand(() => TargetSpeed -= SpeedStep, () => IsConnected);
    InclineUpCommand = new RelayCommand(() => TargetIncline += InclineStep, () => IsConnected);
    InclineDownCommand = new RelayCommand(() => TargetIncline -= InclineStep, () => IsConnected);
  }

  /// <summary>Gets the treadmills found by the last scan.</summary>
  public ObservableCollection<BleDeviceInfo> Devices { get; } = [];

  /// <summary>Gets the Google Health area (sign-in, upload status).</summary>
  public WorkoutSyncViewModel WorkoutSync { get; }

  /// <summary>Gets the diagnostic log shown at the bottom of the window.</summary>
  public ObservableCollection<string> LogEntries { get; } = [];

  /// <summary>Gets the command that scans for treadmills.</summary>
  public ICommand ScanCommand { get; }

  /// <summary>Gets the command that connects to <see cref="SelectedDevice"/>.</summary>
  public ICommand ConnectCommand { get; }

  /// <summary>Gets the command that stops the belt and disconnects.</summary>
  public ICommand DisconnectCommand { get; }

  /// <summary>Gets the command that starts or resumes the belt.</summary>
  public ICommand StartCommand { get; }

  /// <summary>Gets the command that pauses the belt.</summary>
  public ICommand PauseCommand { get; }

  /// <summary>Gets the command that stops the belt.</summary>
  public ICommand StopCommand { get; }

  /// <summary>Gets the command that raises the target speed by one step.</summary>
  public ICommand SpeedUpCommand { get; }

  /// <summary>Gets the command that lowers the target speed by one step.</summary>
  public ICommand SpeedDownCommand { get; }

  /// <summary>Gets the command that raises the target incline by one step.</summary>
  public ICommand InclineUpCommand { get; }

  /// <summary>Gets the command that lowers the target incline by one step.</summary>
  public ICommand InclineDownCommand { get; }

  /// <summary>Gets or sets the treadmill chosen in the device list.</summary>
  public BleDeviceInfo? SelectedDevice
  {
    get => _selectedDevice;
    set => SetProperty(ref _selectedDevice, value);
  }

  /// <summary>Gets a value indicating whether a scan is running.</summary>
  public bool IsScanning
  {
    get => _isScanning;
    private set => SetProperty(ref _isScanning, value);
  }

  /// <summary>Gets a value indicating whether a connection attempt is in progress.</summary>
  public bool IsConnecting
  {
    get => _isConnecting;
    private set => SetProperty(ref _isConnecting, value);
  }

  /// <summary>Gets a value indicating whether a treadmill is connected and under control.</summary>
  public bool IsConnected
  {
    get => _isConnected;
    private set => SetProperty(ref _isConnected, value);
  }

  /// <summary>Gets a value indicating whether the belt is running.</summary>
  public bool IsRunning
  {
    get => _isRunning;
    private set => SetProperty(ref _isRunning, value);
  }

  /// <summary>Gets the one-line status shown below the connection controls.</summary>
  public string StatusText
  {
    get => _statusText;
    private set => SetProperty(ref _statusText, value);
  }

  /// <summary>Gets the lowest selectable speed in km/h.</summary>
  public double MinSpeed
  {
    get => _minSpeed;
    private set => SetProperty(ref _minSpeed, value);
  }

  /// <summary>Gets the highest selectable speed in km/h.</summary>
  public double MaxSpeed
  {
    get => _maxSpeed;
    private set => SetProperty(ref _maxSpeed, value);
  }

  /// <summary>Gets the step between selectable speeds in km/h.</summary>
  public double SpeedStep
  {
    get => _speedStep;
    private set => SetProperty(ref _speedStep, value);
  }

  /// <summary>
  /// Gets or sets the wanted speed in km/h. While the belt runs, a change is sent to the treadmill after the
  /// slider rests; while it is stopped, the value is applied at the next start.
  /// </summary>
  public double TargetSpeed
  {
    get => _targetSpeed;
    set
    {
      double snapped = Snap(value, MinSpeed, MaxSpeed, SpeedStep);
      if (SetProperty(ref _targetSpeed, snapped) && IsRunning)
      {
        _speedDebouncer.Schedule(SendSpeedAsync);
      }
    }
  }

  /// <summary>Gets the lowest selectable incline in percent.</summary>
  public double MinIncline
  {
    get => _minIncline;
    private set => SetProperty(ref _minIncline, value);
  }

  /// <summary>Gets the highest selectable incline in percent.</summary>
  public double MaxIncline
  {
    get => _maxIncline;
    private set => SetProperty(ref _maxIncline, value);
  }

  /// <summary>Gets the step between selectable inclines in percent.</summary>
  public double InclineStep
  {
    get => _inclineStep;
    private set => SetProperty(ref _inclineStep, value);
  }

  /// <summary>
  /// Gets or sets the wanted incline in percent. While connected, a change is sent to the treadmill after the
  /// slider rests.
  /// </summary>
  public double TargetIncline
  {
    get => _targetIncline;
    set
    {
      double snapped = Snap(value, MinIncline, MaxIncline, InclineStep);
      if (SetProperty(ref _targetIncline, snapped) && IsConnected)
      {
        _inclineDebouncer.Schedule(SendInclineAsync);
      }
    }
  }

  /// <summary>Gets the speed reported by the treadmill, formatted for display.</summary>
  public string CurrentSpeedText
  {
    get => _currentSpeedText;
    private set => SetProperty(ref _currentSpeedText, value);
  }

  /// <summary>Gets the incline reported by the treadmill, formatted for display.</summary>
  public string CurrentInclineText
  {
    get => _currentInclineText;
    private set => SetProperty(ref _currentInclineText, value);
  }

  /// <summary>Gets the distance reported by the treadmill, formatted for display.</summary>
  public string DistanceText
  {
    get => _distanceText;
    private set => SetProperty(ref _distanceText, value);
  }

  /// <summary>Gets the elapsed time reported by the treadmill, formatted for display.</summary>
  public string ElapsedText
  {
    get => _elapsedText;
    private set => SetProperty(ref _elapsedText, value);
  }

  /// <summary>Gets the energy reported by the treadmill, formatted for display.</summary>
  public string EnergyText
  {
    get => _energyText;
    private set => SetProperty(ref _energyText, value);
  }

  /// <summary>Gets the heart rate reported by the treadmill, formatted for display.</summary>
  public string HeartRateText
  {
    get => _heartRateText;
    private set => SetProperty(ref _heartRateText, value);
  }

  /// <summary>
  /// Ends the session when the window closes: optionally stops the belt, finishes the workout in progress, waits
  /// a bounded time for its upload, then releases Bluetooth. A workout whose upload does not finish in time stays
  /// archived and is uploaded at the next start.
  /// </summary>
  /// <param name="stopBelt"><see langword="true"/> to stop a running belt before disconnecting.</param>
  public async Task ShutdownAsync(bool stopBelt)
  {
    StopScan();
    _speedDebouncer.Cancel();
    _inclineDebouncer.Cancel();
    if (stopBelt && _client is not null)
    {
      await RunGuardedAsync("Stop",() => _client.StopAsync(_lifetime.Token));
    }

    CompleteWorkout();
    StatusText = "Closing, uploading the workout ...";
    await Task.WhenAny(_uploadTask, Task.Delay(s_exitUploadTimeout));
    _lifetime.Cancel();
    ReleaseClient();
  }

  /// <summary>
  /// Rounds a value to the step grid of its range and limits it to the range.
  /// </summary>
  /// <param name="value">The raw value.</param>
  /// <param name="min">Lower limit.</param>
  /// <param name="max">Upper limit.</param>
  /// <param name="step">Grid size; values of zero or less disable rounding.</param>
  /// <returns>The adjusted value.</returns>
  private static double Snap(double value, double min, double max, double step)
  {
    double rounded = step > 0 ? Math.Round((value - min) / step) * step + min : value;
    return Math.Round(Math.Clamp(rounded, min, max), 2);
  }

  /// <summary>
  /// Runs an action on the UI thread without waiting for it.
  /// </summary>
  /// <param name="action">The action.</param>
  private void PostToUi(Action action)
  {
    _dispatcher.InvokeAsync(action);
  }

  /// <summary>
  /// Appends a timestamped line to the log and trims the log to <see cref="MaxLogEntries"/> lines.
  /// </summary>
  /// <param name="message">The line.</param>
  private void AddLog(string message)
  {
    LogEntries.Add($"{DateTime.Now:HH:mm:ss}  {message}");
    while (LogEntries.Count > MaxLogEntries)
    {
      LogEntries.RemoveAt(0);
    }
  }

  /// <summary>
  /// Runs a Bluetooth action and turns any failure into a log line and a status text, so that commands never throw.
  /// </summary>
  /// <param name="action">Name of the action, used in messages.</param>
  /// <param name="work">The work to run.</param>
  /// <returns><see langword="true"/> if the work succeeded.</returns>
  private async Task<bool> RunGuardedAsync(string action, Func<Task> work)
  {
    try
    {
      await work();
      return true;
    }
    catch (OperationCanceledException)
    {
      return false;
    }
    catch (Exception exception)
    {
      AddLog($"Error during '{action}': {exception.Message}");
      StatusText = $"Error during '{action}': {exception.Message}";
      return false;
    }
  }

  /// <summary>
  /// Scans for treadmills until the scan duration elapsed or <see cref="StopScan"/> is called.
  /// </summary>
  private async Task ScanAsync()
  {
    Devices.Clear();
    SelectedDevice = null;
    _bestRssi.Clear();
    _advertisementCount = 0;
    IsScanning = true;
    StatusText = "Searching for treadmills ...";
    if (!await CheckRadioAsync())
    {
      IsScanning = false;
      _autoConnectPending = false;
      return;
    }

    var cancellation = new CancellationTokenSource();
    _scanCancellation = cancellation;
    _scanner.Start();
    try
    {
      await Task.Delay(s_scanDuration, cancellation.Token);
      StopScan();
      FinishScan();
    }
    catch (OperationCanceledException)
    {
      // StopScan already updated the state.
    }
    finally
    {
      _autoConnectPending = false;
    }
  }

  /// <summary>
  /// Called once when the application starts: scans and connects to the first treadmill whose name identifies
  /// it as a Urevo / Spacewalk. If none shows up during the scan, nothing happens and the user connects manually.
  /// </summary>
  public async Task AutoConnectAsync()
  {
    _autoConnectPending = true;
    await ScanAsync();
  }

  /// <summary>
  /// Connects right away if an automatic connect is pending and the device is a Urevo / Spacewalk.
  /// </summary>
  /// <param name="device">The device that was just listed or updated.</param>
  private void ConnectAutomaticallyIfUrevo(BleDeviceInfo device)
  {
    if (!_autoConnectPending || !HasUrevoName(device))
    {
      return;
    }

    _autoConnectPending = false;
    AddLog($"Urevo treadmill found: {device.Name}. Connecting automatically.");
    SelectedDevice = device;
    _ = ConnectAsync();
  }

  /// <summary>
  /// Tells whether the advertised name contains one of the Urevo name fragments.
  /// </summary>
  /// <param name="device">The device.</param>
  /// <returns><see langword="true"/> if the name identifies a Urevo / Spacewalk treadmill.</returns>
  private static bool HasUrevoName(BleDeviceInfo device)
  {
    return s_preferredNameParts.Any(part => device.Name.Contains(part, StringComparison.OrdinalIgnoreCase));
  }

  /// <summary>
  /// Verifies that Bluetooth is available and reports the reason in the status line if it is not.
  /// </summary>
  /// <returns><see langword="true"/> if scanning can start.</returns>
  private async Task<bool> CheckRadioAsync()
  {
    try
    {
      string? problem = await TreadmillScanner.CheckRadioAsync();
      if (problem is not null)
      {
        AddLog(problem);
        StatusText = problem;
      }

      return problem is null;
    }
    catch (Exception exception)
    {
      AddLog($"Bluetooth check not possible ({exception.Message}); scanning anyway.");
      return true;
    }
  }

  /// <summary>
  /// After a completed scan: sorts the devices by signal strength, preselects the likely treadmill and reports
  /// what was seen, so that an empty list can be told apart from "no Bluetooth signals at all".
  /// </summary>
  private void FinishScan()
  {
    SortDevicesBySignal();
    AddLog($"Scan finished: {Devices.Count} devices, {_advertisementCount} signals received.");
    if (_advertisementCount == 0)
    {
      StatusText = "No Bluetooth signals received. Is Bluetooth on? Is the treadmill in range?";
    }
    else
    {
      StatusText = $"{Devices.Count} devices found. The treadmill usually has the strongest signal (top).";
    }
  }

  /// <summary>
  /// Orders the device list by the best signal strength seen during the scan, strongest first, and keeps or
  /// chooses the selection.
  /// </summary>
  private void SortDevicesBySignal()
  {
    ulong? selectedAddress = SelectedDevice?.Address;
    List<BleDeviceInfo> sorted = Devices
      .Select(device => device with { Rssi = _bestRssi[device.Address] })
      .OrderByDescending(device => device.Rssi)
      .ToList();
    Devices.Clear();
    foreach (BleDeviceInfo device in sorted)
    {
      Devices.Add(device);
    }

    SelectedDevice = sorted.FirstOrDefault(device => device.Address == selectedAddress)
      ?? sorted.FirstOrDefault(IsLikelyTreadmill);
  }

  /// <summary>
  /// Ends a running scan.
  /// </summary>
  private void StopScan()
  {
    _scanCancellation?.Cancel();
    _scanCancellation = null;
    _scanner.Stop();
    IsScanning = false;
  }

  /// <summary>
  /// Adds a found device to the list, preselecting Urevo treadmills.
  /// </summary>
  /// <param name="device">The advertisement's device.</param>
  private void OnDeviceFound(BleDeviceInfo device)
  {
    if (!IsScanning)
    {
      return;
    }

    _advertisementCount++;
    _bestRssi[device.Address] = _bestRssi.TryGetValue(device.Address, out short best)
      ? Math.Max(best, device.Rssi)
      : device.Rssi;

    int index = Devices.ToList().FindIndex(existing => existing.Address == device.Address);
    if (index < 0)
    {
      Devices.Add(device);
    }
    else if (RevealsMore(Devices[index], device))
    {
      bool wasSelected = SelectedDevice == Devices[index];
      Devices[index] = device;
      SelectedDevice = wasSelected ? device : SelectedDevice;
    }
    else
    {
      return;
    }

    if (SelectedDevice is null && IsLikelyTreadmill(device))
    {
      SelectedDevice = device;
    }

    ConnectAutomaticallyIfUrevo(device);
  }

  /// <summary>
  /// Tells whether a newer advertisement of a known device adds information (its name or the FTMS hint) that
  /// the listed entry lacks. The name often arrives only in a later scan response.
  /// </summary>
  /// <param name="listed">The entry in the device list.</param>
  /// <param name="received">The newly received advertisement.</param>
  /// <returns><see langword="true"/> if the list entry should be replaced.</returns>
  private static bool RevealsMore(BleDeviceInfo listed, BleDeviceInfo received)
  {
    bool newName = string.IsNullOrWhiteSpace(listed.Name) && !string.IsNullOrWhiteSpace(received.Name);
    bool newFtms = !listed.AdvertisesFtms && received.AdvertisesFtms;
    return newName || newFtms;
  }

  /// <summary>
  /// Tells whether a device looks like a treadmill: it advertises FTMS or its name contains a known fragment.
  /// </summary>
  /// <param name="device">The device.</param>
  /// <returns><see langword="true"/> if the device is a likely treadmill.</returns>
  private static bool IsLikelyTreadmill(BleDeviceInfo device)
  {
    return device.AdvertisesFtms || HasUrevoName(device);
  }

  /// <summary>
  /// Reports a scan that the system aborted.
  /// </summary>
  /// <param name="reason">The Bluetooth error name.</param>
  private void OnScanFailed(string reason)
  {
    StopScan();
    AddLog($"Scan failed: {reason}. Is Bluetooth switched on?");
    StatusText = "Scan failed. Is Bluetooth switched on?";
  }

  /// <summary>
  /// Connects to the selected treadmill and adopts its speed and incline ranges.
  /// </summary>
  private async Task ConnectAsync()
  {
    BleDeviceInfo? device = SelectedDevice;
    if (device is null)
    {
      return;
    }

    StopScan();
    IsConnecting = true;
    StatusText = $"Connecting to {device.Name} ...";
    var client = new FtmsTreadmillClient();
    client.LogMessage += message => PostToUi(() => AddLog(message));
    client.TelemetryReceived += data => PostToUi(() => OnTelemetry(data));
    client.StatusReceived += kind => PostToUi(() => OnMachineStatus(kind));
    client.Disconnected += () => PostToUi(() => OnClientDisconnected(client));

    bool connected = await RunGuardedAsync("Connect",() => client.ConnectAsync(device.Address, _lifetime.Token));
    if (connected)
    {
      _client = client;
      _connectedDeviceName = device.Name;
      ApplyRanges(client);
      IsConnected = true;
      StatusText = $"Connected to {device.Name}";
    }
    else
    {
      client.Dispose();
    }

    IsConnecting = false;
  }

  /// <summary>
  /// Stops a running belt for safety, then disconnects.
  /// </summary>
  private async Task DisconnectAsync()
  {
    if (IsRunning)
    {
      await StopAsync();
    }

    ReleaseClient();
    StatusText = "Disconnected";
  }

  /// <summary>
  /// Starts the belt, then applies the selected speed and incline.
  /// </summary>
  private async Task StartAsync()
  {
    FtmsTreadmillClient? client = _client;
    if (client is null)
    {
      return;
    }

    bool started = await RunGuardedAsync("Start", () => client.StartAsync(_lifetime.Token));
    if (!started)
    {
      return;
    }

    IsRunning = true;
    _recorder.StartOrResume(DateTimeOffset.Now, _connectedDeviceName);
    StatusText = "Treadmill running";
    await ApplyTargetsAfterStartAsync();
  }

  /// <summary>
  /// Sends the selected incline and speed after a start. The speed is retried once, because many treadmills
  /// ignore commands during their start countdown.
  /// </summary>
  private async Task ApplyTargetsAfterStartAsync()
  {
    await RunGuardedAsync("Incline",SendInclineCoreAsync);
    try
    {
      await Task.Delay(s_startSettleDelay, _lifetime.Token);
      if (!await RunGuardedAsync("Speed",SendSpeedCoreAsync))
      {
        await Task.Delay(s_startRetryDelay, _lifetime.Token);
        await RunGuardedAsync("Speed",SendSpeedCoreAsync);
      }
    }
    catch (OperationCanceledException)
    {
      // The application is shutting down.
    }
  }

  /// <summary>
  /// Pauses the belt.
  /// </summary>
  private async Task PauseAsync()
  {
    FtmsTreadmillClient? client = _client;
    if (client is not null && await RunGuardedAsync("Pause", () => client.PauseAsync(_lifetime.Token)))
    {
      IsRunning = false;
      _recorder.Pause(DateTimeOffset.Now);
      StatusText = "Paused";
    }
  }

  /// <summary>
  /// Stops the belt. Always allowed while connected, as a safety measure.
  /// </summary>
  private async Task StopAsync()
  {
    FtmsTreadmillClient? client = _client;
    _speedDebouncer.Cancel();
    if (client is not null && await RunGuardedAsync("Stop",() => client.StopAsync(_lifetime.Token)))
    {
      IsRunning = false;
      StatusText = "Stopped";
      CompleteWorkout();
    }
  }

  /// <summary>
  /// Finishes the workout in progress (if any), logs its key figures and starts archiving and uploading it.
  /// Accidental short starts are discarded. Safe to call repeatedly for the same stop.
  /// </summary>
  private void CompleteWorkout()
  {
    WorkoutSession? session = _recorder.Finish(DateTimeOffset.Now);
    if (session is null)
    {
      return;
    }

    WorkoutSummary summary = WorkoutAnalyzer.Analyze(session, WorkoutSync.BodyWeightKg);
    if (WorkoutAnalyzer.IsTooShort(summary))
    {
      AddLog("Workout too short (under 1 minute or 20 m), not saved.");
      return;
    }

    AddLog(DescribeWorkout(summary));
    _uploadTask = WorkoutSync.SubmitAsync(session, CancellationToken.None);
  }

  /// <summary>
  /// Formats the key figures of a workout for the log.
  /// </summary>
  /// <param name="summary">The key figures.</param>
  /// <returns>The log line.</returns>
  private static string DescribeWorkout(WorkoutSummary summary)
  {
    CultureInfo culture = CultureInfo.CurrentCulture;
    string energy = summary.EnergyKcal is { } kcal ? $", {kcal.ToString("0", culture)} kcal" : string.Empty;
    return $"Workout finished: {(summary.DistanceMeters / 1000).ToString("0.00", culture)} km in "
      + $"{summary.ActiveDuration:hh\\:mm\\:ss}, avg {summary.AverageSpeedKmh.ToString("0.0", culture)} km/h, "
      + $"avg incline {summary.AverageInclinePercent.ToString("0.0", culture)} %, "
      + $"{summary.ElevationGainMeters.ToString("0", culture)} m climb{energy}";
  }

  /// <summary>
  /// Sends the target speed after the slider rested.
  /// </summary>
  private Task SendSpeedAsync()
  {
    return RunGuardedAsync("Speed",SendSpeedCoreAsync);
  }

  /// <summary>
  /// Sends the target incline after the slider rested.
  /// </summary>
  private Task SendInclineAsync()
  {
    return RunGuardedAsync("Incline",SendInclineCoreAsync);
  }

  /// <summary>
  /// Writes the target speed to the treadmill; throws on failure.
  /// </summary>
  private async Task SendSpeedCoreAsync()
  {
    if (_client is not null)
    {
      await _client.SetSpeedAsync(TargetSpeed, _lifetime.Token);
      AddLog($"Speed set: {TargetSpeed.ToString("0.0", CultureInfo.CurrentCulture)} km/h");
    }
  }

  /// <summary>
  /// Writes the target incline to the treadmill; throws on failure.
  /// </summary>
  private async Task SendInclineCoreAsync()
  {
    if (_client is not null)
    {
      await _client.SetInclineAsync(TargetIncline, _lifetime.Token);
      AddLog($"Incline set: {TargetIncline.ToString("0.#", CultureInfo.CurrentCulture)} %");
    }
  }

  /// <summary>
  /// Adopts the ranges the treadmill reports and re-limits the selected values to them.
  /// </summary>
  /// <param name="client">The connected client.</param>
  private void ApplyRanges(FtmsTreadmillClient client)
  {
    ValueRange speed = client.SpeedRange ?? new ValueRange(DefaultMinSpeedKmh, DefaultMaxSpeedKmh, DefaultSpeedStepKmh);
    ValueRange incline = client.InclineRange ?? new ValueRange(0, DefaultMaxInclinePercent, DefaultInclineStepPercent);
    MaxSpeed = speed.Max;
    MinSpeed = speed.Min;
    SpeedStep = speed.Step > 0 ? speed.Step : DefaultSpeedStepKmh;
    MaxIncline = incline.Max;
    MinIncline = incline.Min;
    InclineStep = incline.Step > 0 ? incline.Step : DefaultInclineStepPercent;
    TargetSpeed = TargetSpeed;
    TargetIncline = TargetIncline;
  }

  /// <summary>
  /// Shows the values the treadmill reports.
  /// </summary>
  /// <param name="data">The telemetry.</param>
  private void OnTelemetry(TreadmillData data)
  {
    _recorder.AddSample(data, DateTimeOffset.Now);
    CultureInfo culture = CultureInfo.CurrentCulture;
    CurrentSpeedText = data.SpeedKmh is { } speed ? $"{speed.ToString("0.0", culture)} km/h" : NoValueText;
    CurrentInclineText = data.InclinePercent is { } incline ? $"{incline.ToString("0.#", culture)} %" : NoValueText;
    DistanceText = data.DistanceMeters is { } meters ? FormatDistance(meters, culture) : NoValueText;
    ElapsedText = data.ElapsedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds).ToString("hh\\:mm\\:ss") : NoValueText;
    EnergyText = data.EnergyKcal is { } energy ? $"{energy} kcal" : NoValueText;
    HeartRateText = data.HeartRate is { } rate ? $"{rate} bpm" : NoValueText;
  }

  /// <summary>
  /// Formats a distance as metres below one kilometre, otherwise as kilometres.
  /// </summary>
  /// <param name="meters">The distance in metres.</param>
  /// <param name="culture">Culture for the decimal separator.</param>
  /// <returns>The display text.</returns>
  private static string FormatDistance(int meters, CultureInfo culture)
  {
    return meters < 1000 ? $"{meters} m" : $"{(meters / 1000.0).ToString("0.00", culture)} km";
  }

  /// <summary>
  /// Follows state changes made on the treadmill itself (console buttons, safety key).
  /// </summary>
  /// <param name="kind">The reported event.</param>
  private void OnMachineStatus(MachineStatusKind kind)
  {
    switch (kind)
    {
      case MachineStatusKind.Started:
        IsRunning = true;
        _recorder.StartOrResume(DateTimeOffset.Now, _connectedDeviceName);
        StatusText = "Treadmill running";
        break;
      case MachineStatusKind.Stopped:
      case MachineStatusKind.Reset:
        IsRunning = false;
        StatusText = "Stopped";
        CompleteWorkout();
        break;
      case MachineStatusKind.Paused:
        IsRunning = false;
        _recorder.Pause(DateTimeOffset.Now);
        StatusText = "Paused";
        break;
      case MachineStatusKind.StoppedBySafetyKey:
        IsRunning = false;
        StatusText = "Safety key removed";
        CompleteWorkout();
        break;
      case MachineStatusKind.ControlPermissionLost:
        StatusText = "Control lost (another client connected?)";
        break;
    }
  }

  /// <summary>
  /// Resets the state after the Bluetooth connection was lost.
  /// </summary>
  /// <param name="client">The client that lost its connection; a stale event of a replaced client is ignored.</param>
  private void OnClientDisconnected(FtmsTreadmillClient client)
  {
    if (ReferenceEquals(client, _client))
    {
      CompleteWorkout();
      ReleaseClient();
      StatusText = "Connection to the treadmill lost";
    }
  }

  /// <summary>
  /// Disposes the client and returns the view to the disconnected state.
  /// </summary>
  private void ReleaseClient()
  {
    _speedDebouncer.Cancel();
    _inclineDebouncer.Cancel();
    _client?.Dispose();
    _client = null;
    IsConnected = false;
    IsRunning = false;
    CurrentSpeedText = NoValueText;
    CurrentInclineText = NoValueText;
    DistanceText = NoValueText;
    ElapsedText = NoValueText;
    EnergyText = NoValueText;
    HeartRateText = NoValueText;
  }
}
