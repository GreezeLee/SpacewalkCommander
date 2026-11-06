using System.Buffers.Binary;

namespace UrevoSpacewalkCommander.Bluetooth;

/// <summary>
/// Constants and encoders/decoders for the Bluetooth SIG Fitness Machine Service (FTMS, 0x1826), the standard
/// protocol that treadmills use to be controlled by apps.
/// </summary>
internal static class FtmsProtocol
{
  /// <summary>Resolution of speed values on the wire: 0.01 km/h per unit.</summary>
  private const double SpeedResolutionKmh = 0.01;

  /// <summary>Resolution of incline values on the wire: 0.1 percent per unit.</summary>
  private const double InclineResolutionPercent = 0.1;

  /// <summary>Treadmill Data flag: when <em>clear</em>, the instantaneous speed field is present.</summary>
  private const ushort FlagMoreData = 1 << 0;

  /// <summary>Treadmill Data flag: average speed field present.</summary>
  private const ushort FlagAverageSpeed = 1 << 1;

  /// <summary>Treadmill Data flag: total distance field present.</summary>
  private const ushort FlagTotalDistance = 1 << 2;

  /// <summary>Treadmill Data flag: inclination and ramp angle fields present.</summary>
  private const ushort FlagInclination = 1 << 3;

  /// <summary>Treadmill Data flag: positive and negative elevation gain fields present.</summary>
  private const ushort FlagElevationGain = 1 << 4;

  /// <summary>Treadmill Data flag: instantaneous pace field present.</summary>
  private const ushort FlagInstantaneousPace = 1 << 5;

  /// <summary>Treadmill Data flag: average pace field present.</summary>
  private const ushort FlagAveragePace = 1 << 6;

  /// <summary>Treadmill Data flag: expended energy fields present.</summary>
  private const ushort FlagExpendedEnergy = 1 << 7;

  /// <summary>Treadmill Data flag: heart rate field present.</summary>
  private const ushort FlagHeartRate = 1 << 8;

  /// <summary>Treadmill Data flag: metabolic equivalent field present.</summary>
  private const ushort FlagMetabolicEquivalent = 1 << 9;

  /// <summary>Treadmill Data flag: elapsed time field present.</summary>
  private const ushort FlagElapsedTime = 1 << 10;

  /// <summary>Value of the "total energy" field that means "not available".</summary>
  private const ushort EnergyNotAvailable = 0xFFFF;

  /// <summary>Value of the "heart rate" field that means "no measurement".</summary>
  private const byte HeartRateNotAvailable = 0;

  /// <summary>The Fitness Machine Service.</summary>
  public static readonly Guid FitnessMachineService = FromShortId(0x1826);

  /// <summary>Notifies decoded treadmill telemetry (speed, distance, time ...).</summary>
  public static readonly Guid TreadmillDataCharacteristic = FromShortId(0x2ACD);

  /// <summary>Accepts control commands (start, stop, target speed, target incline) and indicates the results.</summary>
  public static readonly Guid ControlPointCharacteristic = FromShortId(0x2AD9);

  /// <summary>Notifies state changes of the machine (started, stopped, ...).</summary>
  public static readonly Guid MachineStatusCharacteristic = FromShortId(0x2ADA);

  /// <summary>Holds the supported speed range.</summary>
  public static readonly Guid SupportedSpeedRangeCharacteristic = FromShortId(0x2AD4);

  /// <summary>Holds the supported incline range.</summary>
  public static readonly Guid SupportedInclinationRangeCharacteristic = FromShortId(0x2AD5);

  /// <summary>Op code of the "response" indication sent by the control point.</summary>
  public const byte ResponseOpCode = 0x80;

  /// <summary>Control point op code: take control of the machine.</summary>
  public const byte OpCodeRequestControl = 0x00;

  /// <summary>Control point op code: set the target speed.</summary>
  public const byte OpCodeSetTargetSpeed = 0x02;

  /// <summary>Control point op code: set the target inclination.</summary>
  public const byte OpCodeSetTargetInclination = 0x03;

  /// <summary>Control point op code: start or resume the belt.</summary>
  public const byte OpCodeStartOrResume = 0x07;

  /// <summary>Control point op code: stop or pause the belt.</summary>
  public const byte OpCodeStopOrPause = 0x08;

  /// <summary>Parameter of <see cref="OpCodeStopOrPause"/>: stop.</summary>
  public const byte StopParameter = 0x01;

  /// <summary>Parameter of <see cref="OpCodeStopOrPause"/>: pause.</summary>
  public const byte PauseParameter = 0x02;

  /// <summary>
  /// Expands a 16-bit Bluetooth SIG short id to the full 128-bit UUID.
  /// </summary>
  /// <param name="shortId">The 16-bit assigned number.</param>
  /// <returns>The UUID based on the Bluetooth base UUID.</returns>
  public static Guid FromShortId(ushort shortId)
  {
    return new Guid($"0000{shortId:x4}-0000-1000-8000-00805f9b34fb");
  }

  /// <summary>
  /// Creates the control point command that sets the target belt speed.
  /// </summary>
  /// <param name="speedKmh">Target speed in km/h.</param>
  /// <returns>The bytes to write to the control point.</returns>
  public static byte[] CreateSetTargetSpeed(double speedKmh)
  {
    var command = new byte[3];
    command[0] = OpCodeSetTargetSpeed;
    BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(1), (ushort)Math.Round(speedKmh / SpeedResolutionKmh));
    return command;
  }

  /// <summary>
  /// Creates the control point command that sets the target incline.
  /// </summary>
  /// <param name="inclinePercent">Target incline in percent.</param>
  /// <returns>The bytes to write to the control point.</returns>
  public static byte[] CreateSetTargetInclination(double inclinePercent)
  {
    var command = new byte[3];
    command[0] = OpCodeSetTargetInclination;
    BinaryPrimitives.WriteInt16LittleEndian(
      command.AsSpan(1),
      (short)Math.Round(inclinePercent / InclineResolutionPercent));
    return command;
  }

  /// <summary>
  /// Decodes a Treadmill Data notification. Fields after the elapsed time are not needed and not read.
  /// </summary>
  /// <param name="data">The raw characteristic value.</param>
  /// <returns>The decoded telemetry.</returns>
  /// <exception cref="FormatException">The value is shorter than its flags announce.</exception>
  public static TreadmillData ParseTreadmillData(ReadOnlySpan<byte> data)
  {
    var reader = new FtmsByteReader(data);
    ushort flags = reader.ReadUInt16();
    double? speed = null;
    double? incline = null;
    int? distance = null;
    int? elapsed = null;
    int? energy = null;
    int? heartRate = null;

    if ((flags & FlagMoreData) == 0)
    {
      speed = reader.ReadUInt16() * SpeedResolutionKmh;
    }

    if ((flags & FlagAverageSpeed) != 0)
    {
      reader.Skip(2);
    }

    if ((flags & FlagTotalDistance) != 0)
    {
      distance = reader.ReadUInt24();
    }

    if ((flags & FlagInclination) != 0)
    {
      incline = reader.ReadInt16() * InclineResolutionPercent;
      reader.Skip(2);
    }

    if ((flags & FlagElevationGain) != 0)
    {
      reader.Skip(4);
    }

    if ((flags & FlagInstantaneousPace) != 0)
    {
      reader.Skip(1);
    }

    if ((flags & FlagAveragePace) != 0)
    {
      reader.Skip(1);
    }

    if ((flags & FlagExpendedEnergy) != 0)
    {
      ushort totalEnergy = reader.ReadUInt16();
      energy = totalEnergy == EnergyNotAvailable ? null : totalEnergy;
      reader.Skip(3);
    }

    if ((flags & FlagHeartRate) != 0)
    {
      byte rate = reader.ReadByte();
      heartRate = rate == HeartRateNotAvailable ? null : rate;
    }

    if ((flags & FlagMetabolicEquivalent) != 0)
    {
      reader.Skip(1);
    }

    if ((flags & FlagElapsedTime) != 0)
    {
      elapsed = reader.ReadUInt16();
    }

    return new TreadmillData(speed, incline, distance, elapsed, energy, heartRate);
  }

  /// <summary>
  /// Decodes the Supported Speed Range characteristic.
  /// </summary>
  /// <param name="data">The raw characteristic value (minimum, maximum, step; each 0.01 km/h).</param>
  /// <returns>The range in km/h.</returns>
  /// <exception cref="FormatException">The value is shorter than 6 bytes.</exception>
  public static ValueRange ParseSpeedRange(ReadOnlySpan<byte> data)
  {
    var reader = new FtmsByteReader(data);
    double min = reader.ReadUInt16() * SpeedResolutionKmh;
    double max = reader.ReadUInt16() * SpeedResolutionKmh;
    double step = reader.ReadUInt16() * SpeedResolutionKmh;
    return new ValueRange(min, max, step);
  }

  /// <summary>
  /// Decodes the Supported Inclination Range characteristic.
  /// </summary>
  /// <param name="data">The raw characteristic value (minimum, maximum, step; each 0.1 percent).</param>
  /// <returns>The range in percent.</returns>
  /// <exception cref="FormatException">The value is shorter than 6 bytes.</exception>
  public static ValueRange ParseInclinationRange(ReadOnlySpan<byte> data)
  {
    var reader = new FtmsByteReader(data);
    double min = reader.ReadInt16() * InclineResolutionPercent;
    double max = reader.ReadInt16() * InclineResolutionPercent;
    double step = reader.ReadUInt16() * InclineResolutionPercent;
    return new ValueRange(min, max, step);
  }

  /// <summary>
  /// Decodes a Fitness Machine Status notification.
  /// </summary>
  /// <param name="data">The raw characteristic value; the first byte is the event op code.</param>
  /// <returns>The kind of event; <see cref="MachineStatusKind.Unknown"/> for empty or unknown values.</returns>
  public static MachineStatusKind ParseMachineStatus(ReadOnlySpan<byte> data)
  {
    if (data.IsEmpty)
    {
      return MachineStatusKind.Unknown;
    }

    return data[0] switch
    {
      0x01 => MachineStatusKind.Reset,
      0x02 => data.Length > 1 && data[1] == PauseParameter ? MachineStatusKind.Paused : MachineStatusKind.Stopped,
      0x03 => MachineStatusKind.StoppedBySafetyKey,
      0x04 => MachineStatusKind.Started,
      0x05 => MachineStatusKind.TargetSpeedChanged,
      0x06 => MachineStatusKind.TargetInclineChanged,
      0xFF => MachineStatusKind.ControlPermissionLost,
      _ => MachineStatusKind.Unknown,
    };
  }

  /// <summary>
  /// Little-endian reader over a characteristic value that fails with a <see cref="FormatException"/> instead of
  /// reading past the end.
  /// </summary>
  private ref struct FtmsByteReader
  {
    /// <summary>The bytes being read.</summary>
    private readonly ReadOnlySpan<byte> _data;

    /// <summary>Index of the next byte to read.</summary>
    private int _position;

    /// <summary>
    /// Initializes a reader at the start of <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The bytes to read.</param>
    public FtmsByteReader(ReadOnlySpan<byte> data)
    {
      _data = data;
      _position = 0;
    }

    /// <summary>Reads one byte.</summary>
    /// <returns>The byte.</returns>
    public byte ReadByte()
    {
      Ensure(1);
      return _data[_position++];
    }

    /// <summary>Reads an unsigned 16-bit value.</summary>
    /// <returns>The value.</returns>
    public ushort ReadUInt16()
    {
      Ensure(2);
      ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_position));
      _position += 2;
      return value;
    }

    /// <summary>Reads a signed 16-bit value.</summary>
    /// <returns>The value.</returns>
    public short ReadInt16()
    {
      Ensure(2);
      short value = BinaryPrimitives.ReadInt16LittleEndian(_data.Slice(_position));
      _position += 2;
      return value;
    }

    /// <summary>Reads an unsigned 24-bit value.</summary>
    /// <returns>The value.</returns>
    public int ReadUInt24()
    {
      Ensure(3);
      int value = _data[_position] | (_data[_position + 1] << 8) | (_data[_position + 2] << 16);
      _position += 3;
      return value;
    }

    /// <summary>Skips bytes of fields that are not needed.</summary>
    /// <param name="count">Number of bytes to skip.</param>
    public void Skip(int count)
    {
      Ensure(count);
      _position += count;
    }

    /// <summary>Throws if fewer than <paramref name="count"/> bytes remain.</summary>
    /// <param name="count">Number of bytes about to be consumed.</param>
    private readonly void Ensure(int count)
    {
      if (_data.Length - _position < count)
      {
        throw new FormatException("The FTMS value is shorter than expected.");
      }
    }
  }
}
