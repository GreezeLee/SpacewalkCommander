using UrevoSpacewalkCommander.Bluetooth;

namespace UrevoSpacewalkCommander.Tests;

/// <summary>
/// Tests the FTMS encoders and decoders against hand-built byte sequences from the FTMS specification.
/// </summary>
public sealed class FtmsProtocolTests
{
  /// <summary>Target speed is little-endian in 0.01 km/h.</summary>
  [Fact]
  public void CreateSetTargetSpeed_EncodesHundredthsOfKmh()
  {
    Assert.Equal(new byte[] { 0x02, 0x5E, 0x01 }, FtmsProtocol.CreateSetTargetSpeed(3.5));
  }

  /// <summary>Target incline is signed little-endian in 0.1 %.</summary>
  [Fact]
  public void CreateSetTargetInclination_EncodesSignedTenthsOfPercent()
  {
    Assert.Equal(new byte[] { 0x03, 0xF1, 0xFF }, FtmsProtocol.CreateSetTargetInclination(-1.5));
  }

  /// <summary>Speed, distance, incline and elapsed time are decoded in the field order of the specification.</summary>
  [Fact]
  public void ParseTreadmillData_DecodesPresentFields()
  {
    // Flags 0x040C: total distance (bit 2), inclination (bit 3), elapsed time (bit 10); speed present (bit 0 clear).
    byte[] data =
    [
      0x0C, 0x04,
      0x5E, 0x01,
      0xE8, 0x03, 0x00,
      0x32, 0x00, 0x00, 0x00,
      0x3C, 0x00,
    ];

    TreadmillData result = FtmsProtocol.ParseTreadmillData(data);

    Assert.Equal(3.5, result.SpeedKmh!.Value, precision: 6);
    Assert.Equal(1000, result.DistanceMeters);
    Assert.Equal(5.0, result.InclinePercent!.Value, precision: 6);
    Assert.Equal(60, result.ElapsedSeconds);
    Assert.Null(result.HeartRate);
  }

  /// <summary>A notification with the "more data" flag carries no speed.</summary>
  [Fact]
  public void ParseTreadmillData_MoreDataFlagMeansNoSpeed()
  {
    TreadmillData result = FtmsProtocol.ParseTreadmillData([0x05, 0x00, 0x10, 0x00, 0x00]);

    Assert.Null(result.SpeedKmh);
    Assert.Equal(16, result.DistanceMeters);
  }

  /// <summary>A truncated notification is rejected instead of being misread.</summary>
  [Fact]
  public void ParseTreadmillData_RejectsTruncatedValue()
  {
    Assert.Throws<FormatException>(() => FtmsProtocol.ParseTreadmillData([0x00, 0x00, 0x5E]));
  }

  /// <summary>The stop/pause status distinguishes pause by its parameter.</summary>
  [Fact]
  public void ParseMachineStatus_DistinguishesPauseAndStop()
  {
    Assert.Equal(MachineStatusKind.Paused, FtmsProtocol.ParseMachineStatus([0x02, 0x02]));
    Assert.Equal(MachineStatusKind.Stopped, FtmsProtocol.ParseMachineStatus([0x02, 0x01]));
    Assert.Equal(MachineStatusKind.Started, FtmsProtocol.ParseMachineStatus([0x04]));
  }
}
