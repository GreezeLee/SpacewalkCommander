using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace SpacewalkCommander.Bluetooth;

/// <summary>
/// Controls one treadmill through the Bluetooth LE Fitness Machine Service (FTMS).
/// </summary>
/// <remarks>
/// All events are raised on background threads. Every exchange with the device is bounded by a timeout, because
/// the Windows Bluetooth stack otherwise may wait indefinitely for a device that went out of range.
/// </remarks>
internal sealed class FtmsTreadmillClient : IDisposable
{
  /// <summary>FTMS result code for a successfully executed command.</summary>
  private const byte FtmsResultSuccess = 0x01;

  /// <summary>Upper bound for connecting to the device.</summary>
  private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

  /// <summary>Upper bound for a single GATT read, write or discovery.</summary>
  private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

  /// <summary>How long to wait for the control point indication that confirms a command.</summary>
  private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);

  /// <summary>Serializes control point commands, because FTMS allows only one outstanding procedure.</summary>
  private readonly SemaphoreSlim _commandLock = new(1, 1);

  /// <summary>The command currently waiting for its indication; <see langword="null"/> if none.</summary>
  private volatile PendingCommand? _pending;

  /// <summary>The connected device.</summary>
  private BluetoothLEDevice? _device;

  /// <summary>The FTMS service; kept referenced because releasing it can close the connection.</summary>
  private GattDeviceService? _service;

  /// <summary>The control point characteristic that receives commands.</summary>
  private GattCharacteristic? _controlPoint;

  /// <summary>The telemetry characteristic.</summary>
  private GattCharacteristic? _treadmillData;

  /// <summary>The state change characteristic.</summary>
  private GattCharacteristic? _machineStatus;

  /// <summary>Set once the client was disposed; afterwards no events are raised.</summary>
  private volatile bool _disposed;

  /// <summary>Raised for every telemetry notification.</summary>
  public event Action<TreadmillData>? TelemetryReceived;

  /// <summary>Raised when the treadmill reports a state change.</summary>
  public event Action<MachineStatusKind>? StatusReceived;

  /// <summary>Raised when the Bluetooth connection to the treadmill was lost.</summary>
  public event Action? Disconnected;

  /// <summary>Raised with human-readable diagnostic lines.</summary>
  public event Action<string>? LogMessage;

  /// <summary>Gets the speed range reported by the treadmill, or <see langword="null"/> if it does not report one.</summary>
  public ValueRange? SpeedRange { get; private set; }

  /// <summary>Gets the incline range reported by the treadmill, or <see langword="null"/> if it does not report one.</summary>
  public ValueRange? InclineRange { get; private set; }

  /// <summary>
  /// Connects to the treadmill, discovers FTMS, subscribes to its notifications and requests control.
  /// </summary>
  /// <param name="bluetoothAddress">The 48-bit Bluetooth address of the treadmill.</param>
  /// <param name="cancellationToken">Cancels the connection attempt.</param>
  /// <exception cref="NotSupportedException">The device does not offer FTMS or its control point.</exception>
  /// <exception cref="TimeoutException">The device did not answer in time.</exception>
  /// <exception cref="IOException">The Bluetooth stack reported a failure.</exception>
  public async Task ConnectAsync(ulong bluetoothAddress, CancellationToken cancellationToken)
  {
    Log($"Connecting to {BleDeviceInfo.FormatAddress(bluetoothAddress)} ...");
    _device = await RunAsync(
      BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress),
      ConnectTimeout,
      "opening the device",
      cancellationToken).ConfigureAwait(false)
      ?? throw new IOException("The device could not be opened.");
    _device.ConnectionStatusChanged += OnConnectionStatusChanged;

    await DiscoverFtmsAsync(_device, cancellationToken).ConfigureAwait(false);
    await SubscribeAsync(cancellationToken).ConfigureAwait(false);
    await ReadRangesAsync(cancellationToken).ConfigureAwait(false);
    await RequestControlAsync(cancellationToken).ConfigureAwait(false);
    Log("Connected, control acquired.");
  }

  /// <summary>
  /// Takes control of the treadmill, then starts or resumes the belt.
  /// </summary>
  /// <param name="cancellationToken">Cancels the operation.</param>
  public async Task StartAsync(CancellationToken cancellationToken)
  {
    await RequestControlAsync(cancellationToken).ConfigureAwait(false);
    await SendControlAsync([FtmsProtocol.OpCodeStartOrResume], cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Stops the belt and ends the session.
  /// </summary>
  /// <param name="cancellationToken">Cancels the operation.</param>
  public Task StopAsync(CancellationToken cancellationToken)
  {
    return SendControlAsync([FtmsProtocol.OpCodeStopOrPause, FtmsProtocol.StopParameter], cancellationToken);
  }

  /// <summary>
  /// Pauses the belt; <see cref="StartAsync"/> resumes it.
  /// </summary>
  /// <param name="cancellationToken">Cancels the operation.</param>
  public Task PauseAsync(CancellationToken cancellationToken)
  {
    return SendControlAsync([FtmsProtocol.OpCodeStopOrPause, FtmsProtocol.PauseParameter], cancellationToken);
  }

  /// <summary>
  /// Sets the target belt speed.
  /// </summary>
  /// <param name="speedKmh">Target speed in km/h.</param>
  /// <param name="cancellationToken">Cancels the operation.</param>
  public Task SetSpeedAsync(double speedKmh, CancellationToken cancellationToken)
  {
    return SendControlAsync(FtmsProtocol.CreateSetTargetSpeed(speedKmh), cancellationToken);
  }

  /// <summary>
  /// Sets the target incline.
  /// </summary>
  /// <param name="inclinePercent">Target incline in percent.</param>
  /// <param name="cancellationToken">Cancels the operation.</param>
  public Task SetInclineAsync(double inclinePercent, CancellationToken cancellationToken)
  {
    return SendControlAsync(FtmsProtocol.CreateSetTargetInclination(inclinePercent), cancellationToken);
  }

  /// <summary>
  /// Releases the connection. The treadmill is not stopped; call <see cref="StopAsync"/> first if needed.
  /// </summary>
  public void Dispose()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    if (_device is not null)
    {
      _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
    }

    if (_treadmillData is not null)
    {
      _treadmillData.ValueChanged -= OnTreadmillDataChanged;
    }

    if (_machineStatus is not null)
    {
      _machineStatus.ValueChanged -= OnMachineStatusChanged;
    }

    if (_controlPoint is not null)
    {
      _controlPoint.ValueChanged -= OnControlPointChanged;
    }

    _service?.Dispose();
    _device?.Dispose();
    _commandLock.Dispose();
  }

  /// <summary>
  /// Awaits a Windows Runtime operation with a timeout. A timeout is reported as <see cref="TimeoutException"/>;
  /// only the caller's own cancellation surfaces as <see cref="OperationCanceledException"/>.
  /// </summary>
  /// <typeparam name="T">Result type of the operation.</typeparam>
  /// <param name="operation">The operation to await.</param>
  /// <param name="timeout">Upper bound for the operation.</param>
  /// <param name="description">What the operation does, used in the timeout message.</param>
  /// <param name="cancellationToken">The caller's cancellation token.</param>
  /// <returns>The operation result.</returns>
  private static async Task<T> RunAsync<T>(
    IAsyncOperation<T> operation,
    TimeSpan timeout,
    string description,
    CancellationToken cancellationToken)
  {
    using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeoutSource.CancelAfter(timeout);
    try
    {
      return await operation.AsTask(timeoutSource.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      throw new TimeoutException($"Timeout while {description}.");
    }
  }

  /// <summary>
  /// Awaits a Windows Runtime operation with the default <see cref="OperationTimeout"/>.
  /// </summary>
  /// <typeparam name="T">Result type of the operation.</typeparam>
  /// <param name="operation">The operation to await.</param>
  /// <param name="description">What the operation does, used in the timeout message.</param>
  /// <param name="cancellationToken">The caller's cancellation token.</param>
  /// <returns>The operation result.</returns>
  private static Task<T> RunAsync<T>(IAsyncOperation<T> operation, string description, CancellationToken cancellationToken)
  {
    return RunAsync(operation, OperationTimeout, description, cancellationToken);
  }

  /// <summary>
  /// Copies a Windows Runtime buffer into a byte array.
  /// </summary>
  /// <param name="buffer">The buffer.</param>
  /// <returns>The bytes.</returns>
  private static byte[] ToBytes(IBuffer buffer)
  {
    var bytes = new byte[buffer.Length];
    DataReader.FromBuffer(buffer).ReadBytes(bytes);
    return bytes;
  }

  /// <summary>
  /// Wraps bytes into a Windows Runtime buffer.
  /// </summary>
  /// <param name="bytes">The bytes.</param>
  /// <returns>The buffer.</returns>
  private static IBuffer ToBuffer(byte[] bytes)
  {
    var writer = new DataWriter();
    writer.WriteBytes(bytes);
    return writer.DetachBuffer();
  }

  /// <summary>
  /// Finds the FTMS service and its characteristics. Logs all services, so that a treadmill with a different
  /// protocol can be recognized from the log.
  /// </summary>
  /// <param name="device">The connected device.</param>
  /// <param name="cancellationToken">Cancels the operation.</param>
  /// <exception cref="NotSupportedException">FTMS or its control point is missing.</exception>
  private async Task DiscoverFtmsAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
  {
    GattDeviceServicesResult services = await RunAsync(
      device.GetGattServicesAsync(BluetoothCacheMode.Uncached),
      "discovering services",
      cancellationToken).ConfigureAwait(false);
    EnsureSuccess(services.Status, "service discovery");

    string serviceList = string.Join(", ", services.Services.Select(service => service.Uuid.ToString()));
    Log($"Services: {serviceList}");
    _service = services.Services.FirstOrDefault(service => service.Uuid == FtmsProtocol.FitnessMachineService);
    foreach (GattDeviceService other in services.Services.Where(service => service != _service))
    {
      other.Dispose();
    }

    if (_service is null)
    {
      throw new NotSupportedException(
        "The device does not offer the Fitness Machine Service (0x1826). Found services: " + serviceList);
    }

    GattCharacteristicsResult characteristics = await RunAsync(
      _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached),
      "discovering characteristics",
      cancellationToken).ConfigureAwait(false);
    EnsureSuccess(characteristics.Status, "characteristic discovery");
    _controlPoint = FindCharacteristic(characteristics, FtmsProtocol.ControlPointCharacteristic);
    _treadmillData = FindCharacteristic(characteristics, FtmsProtocol.TreadmillDataCharacteristic);
    _machineStatus = FindCharacteristic(characteristics, FtmsProtocol.MachineStatusCharacteristic);
    if (_controlPoint is null)
    {
      throw new NotSupportedException("The Fitness Machine Service has no control point characteristic.");
    }
  }

  /// <summary>
  /// Subscribes to control point indications and to the telemetry and status notifications.
  /// </summary>
  /// <param name="cancellationToken">Cancels the operation.</param>
  private async Task SubscribeAsync(CancellationToken cancellationToken)
  {
    _controlPoint!.ValueChanged += OnControlPointChanged;
    await EnableAsync(
      _controlPoint,
      GattClientCharacteristicConfigurationDescriptorValue.Indicate,
      cancellationToken).ConfigureAwait(false);

    if (_treadmillData is not null)
    {
      _treadmillData.ValueChanged += OnTreadmillDataChanged;
      await EnableAsync(
        _treadmillData,
        GattClientCharacteristicConfigurationDescriptorValue.Notify,
        cancellationToken).ConfigureAwait(false);
    }
    else
    {
      Log("Note: the treadmill does not send Treadmill Data values.");
    }

    if (_machineStatus is not null)
    {
      _machineStatus.ValueChanged += OnMachineStatusChanged;
      await EnableAsync(
        _machineStatus,
        GattClientCharacteristicConfigurationDescriptorValue.Notify,
        cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Reads the supported speed and incline ranges. Missing or unreadable ranges are not an error; the caller
  /// falls back to defaults.
  /// </summary>
  /// <param name="cancellationToken">Cancels the operation.</param>
  private async Task ReadRangesAsync(CancellationToken cancellationToken)
  {
    byte[]? speed = await TryReadAsync(FtmsProtocol.SupportedSpeedRangeCharacteristic, cancellationToken)
      .ConfigureAwait(false);
    byte[]? incline = await TryReadAsync(FtmsProtocol.SupportedInclinationRangeCharacteristic, cancellationToken)
      .ConfigureAwait(false);
    try
    {
      SpeedRange = speed is null ? null : FtmsProtocol.ParseSpeedRange(speed);
      InclineRange = incline is null ? null : FtmsProtocol.ParseInclinationRange(incline);
    }
    catch (FormatException)
    {
      Log("Note: the treadmill's value ranges could not be read.");
    }

    Log($"Speed range: {Describe(SpeedRange)} km/h, incline range: {Describe(InclineRange)} %");
  }

  /// <summary>
  /// Takes control of the treadmill, which FTMS requires before any other command.
  /// </summary>
  /// <param name="cancellationToken">Cancels the operation.</param>
  private Task RequestControlAsync(CancellationToken cancellationToken)
  {
    return SendControlAsync([FtmsProtocol.OpCodeRequestControl], cancellationToken);
  }

  /// <summary>
  /// Writes a command to the control point and waits for the treadmill's confirmation.
  /// </summary>
  /// <param name="command">The command bytes; the first byte is the op code.</param>
  /// <param name="cancellationToken">Cancels the operation.</param>
  /// <exception cref="InvalidOperationException">The treadmill rejected the command.</exception>
  private async Task SendControlAsync(byte[] command, CancellationToken cancellationToken)
  {
    GattCharacteristic controlPoint = _controlPoint ?? throw new InvalidOperationException("Not connected.");
    await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      var pending = new PendingCommand(command[0]);
      _pending = pending;
      GattWriteResult result = await RunAsync(
        controlPoint.WriteValueWithResultAsync(ToBuffer(command), GattWriteOption.WriteWithResponse),
        "sending a command",
        cancellationToken).ConfigureAwait(false);
      EnsureSuccess(result.Status, "sending a command");

      byte? resultCode = await WaitForResponseAsync(pending, cancellationToken).ConfigureAwait(false);
      if (resultCode is null)
      {
        Log($"Note: no confirmation for command 0x{command[0]:X2}.");
      }
      else if (resultCode != FtmsResultSuccess)
      {
        throw new InvalidOperationException(
          $"The treadmill rejected command 0x{command[0]:X2} (result {DescribeResult(resultCode.Value)}).");
      }
    }
    finally
    {
      _pending = null;
      _commandLock.Release();
    }
  }

  /// <summary>
  /// Waits for the control point indication that answers <paramref name="pending"/>.
  /// </summary>
  /// <param name="pending">The command whose answer is awaited.</param>
  /// <param name="cancellationToken">Cancels the wait.</param>
  /// <returns>The FTMS result code, or <see langword="null"/> if the treadmill did not answer in time.</returns>
  private static async Task<byte?> WaitForResponseAsync(PendingCommand pending, CancellationToken cancellationToken)
  {
    Task finished = await Task.WhenAny(pending.Completion.Task, Task.Delay(ResponseTimeout, cancellationToken))
      .ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return finished == pending.Completion.Task ? await pending.Completion.Task.ConfigureAwait(false) : null;
  }

  /// <summary>
  /// Enables notifications or indications on a characteristic.
  /// </summary>
  /// <param name="characteristic">The characteristic.</param>
  /// <param name="mode">Notify or indicate.</param>
  /// <param name="cancellationToken">Cancels the operation.</param>
  private async Task EnableAsync(
    GattCharacteristic characteristic,
    GattClientCharacteristicConfigurationDescriptorValue mode,
    CancellationToken cancellationToken)
  {
    GattCommunicationStatus status = await RunAsync(
      characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(mode),
      "subscribing",
      cancellationToken).ConfigureAwait(false);
    if (status != GattCommunicationStatus.Success)
    {
      Log($"Note: subscribing to {characteristic.Uuid} failed ({status}).");
    }
  }

  /// <summary>
  /// Reads an optional characteristic of the FTMS service.
  /// </summary>
  /// <param name="uuid">The characteristic to read.</param>
  /// <param name="cancellationToken">Cancels the operation.</param>
  /// <returns>The value, or <see langword="null"/> if the characteristic is missing or unreadable.</returns>
  private async Task<byte[]?> TryReadAsync(Guid uuid, CancellationToken cancellationToken)
  {
    try
    {
      GattCharacteristicsResult found = await RunAsync(
        _service!.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached),
        "looking up a characteristic",
        cancellationToken).ConfigureAwait(false);
      GattCharacteristic? characteristic = FindCharacteristic(found, uuid);
      if (characteristic is null)
      {
        return null;
      }

      GattReadResult read = await RunAsync(
        characteristic.ReadValueAsync(BluetoothCacheMode.Uncached),
        "reading a characteristic",
        cancellationToken).ConfigureAwait(false);
      return read.Status == GattCommunicationStatus.Success ? ToBytes(read.Value) : null;
    }
    catch (TimeoutException)
    {
      return null;
    }
  }

  /// <summary>
  /// Picks a characteristic out of a discovery result.
  /// </summary>
  /// <param name="result">The discovery result.</param>
  /// <param name="uuid">The wanted characteristic.</param>
  /// <returns>The characteristic, or <see langword="null"/> if the discovery failed or did not contain it.</returns>
  private static GattCharacteristic? FindCharacteristic(GattCharacteristicsResult result, Guid uuid)
  {
    return result.Status == GattCommunicationStatus.Success
      ? result.Characteristics.FirstOrDefault(characteristic => characteristic.Uuid == uuid)
      : null;
  }

  /// <summary>
  /// Throws if a GATT operation did not succeed.
  /// </summary>
  /// <param name="status">The reported status.</param>
  /// <param name="description">What was attempted.</param>
  /// <exception cref="IOException">The status is not <see cref="GattCommunicationStatus.Success"/>.</exception>
  private static void EnsureSuccess(GattCommunicationStatus status, string description)
  {
    if (status != GattCommunicationStatus.Success)
    {
      throw new IOException($"Bluetooth failure during {description}: {status}.");
    }
  }

  /// <summary>
  /// Formats a range for the log.
  /// </summary>
  /// <param name="range">The range, or <see langword="null"/> if unknown.</param>
  /// <returns>Text such as <c>1.0 - 6.0</c>, or <c>?</c>.</returns>
  private static string Describe(ValueRange? range)
  {
    return range is null ? "?" : FormattableString.Invariant($"{range.Min:0.0} - {range.Max:0.0}");
  }

  /// <summary>
  /// Names an FTMS result code for error messages.
  /// </summary>
  /// <param name="code">The result code.</param>
  /// <returns>A readable name.</returns>
  private static string DescribeResult(byte code)
  {
    return code switch
    {
      2 => "op code not supported",
      3 => "invalid parameter",
      4 => "operation failed",
      5 => "control not permitted",
      _ => $"0x{code:X2}",
    };
  }

  /// <summary>
  /// Decodes a telemetry notification and forwards it.
  /// </summary>
  /// <param name="sender">The characteristic.</param>
  /// <param name="args">The notification.</param>
  private void OnTreadmillDataChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
  {
    try
    {
      TreadmillData data = FtmsProtocol.ParseTreadmillData(ToBytes(args.CharacteristicValue));
      RaiseIfActive(() => TelemetryReceived?.Invoke(data));
    }
    catch (FormatException)
    {
      Log("Note: invalid Treadmill Data notification ignored.");
    }
  }

  /// <summary>
  /// Decodes a state change notification and forwards it.
  /// </summary>
  /// <param name="sender">The characteristic.</param>
  /// <param name="args">The notification.</param>
  private void OnMachineStatusChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
  {
    MachineStatusKind kind = FtmsProtocol.ParseMachineStatus(ToBytes(args.CharacteristicValue));
    Log($"Status: {kind}");
    RaiseIfActive(() => StatusReceived?.Invoke(kind));
  }

  /// <summary>
  /// Matches a control point indication to the command waiting for it.
  /// </summary>
  /// <param name="sender">The characteristic.</param>
  /// <param name="args">The indication: response op code, request op code, result code.</param>
  private void OnControlPointChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
  {
    byte[] data = ToBytes(args.CharacteristicValue);
    PendingCommand? pending = _pending;
    if (data.Length >= 3 && data[0] == FtmsProtocol.ResponseOpCode && pending is not null && pending.OpCode == data[1])
    {
      pending.Completion.TrySetResult(data[2]);
    }
  }

  /// <summary>
  /// Reports a lost connection to the owner.
  /// </summary>
  /// <param name="sender">The device.</param>
  /// <param name="args">Unused.</param>
  private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
  {
    if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
    {
      Log("Connection to the treadmill lost.");
      RaiseIfActive(() => Disconnected?.Invoke());
    }
  }

  /// <summary>
  /// Raises an event unless the client was disposed in the meantime.
  /// </summary>
  /// <param name="raise">The event invocation.</param>
  private void RaiseIfActive(Action raise)
  {
    if (!_disposed)
    {
      raise();
    }
  }

  /// <summary>
  /// Raises <see cref="LogMessage"/>.
  /// </summary>
  /// <param name="message">The line to log.</param>
  private void Log(string message)
  {
    RaiseIfActive(() => LogMessage?.Invoke(message));
  }

  /// <summary>
  /// A command written to the control point whose indication has not arrived yet.
  /// </summary>
  /// <param name="OpCode">Op code of the command.</param>
  private sealed record PendingCommand(byte OpCode)
  {
    /// <summary>Completed with the FTMS result code when the matching indication arrives.</summary>
    public TaskCompletionSource<byte> Completion { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
  }
}
