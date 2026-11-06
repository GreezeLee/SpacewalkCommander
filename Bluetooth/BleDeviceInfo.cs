namespace SpacewalkCommander.Bluetooth;

/// <summary>
/// A Bluetooth LE device found while scanning.
/// </summary>
/// <param name="Address">48-bit Bluetooth address of the device.</param>
/// <param name="Name">Advertised local name, empty if the device did not advertise one.</param>
/// <param name="AdvertisesFtms">
/// <see langword="true"/> if the device lists the Fitness Machine Service in its advertisement.
/// </param>
/// <param name="Rssi">
/// Signal strength in dBm when the device was last seen; closer to zero means closer to the PC. A treadmill
/// next to the PC typically shows -30 to -60.
/// </param>
internal sealed record BleDeviceInfo(ulong Address, string Name, bool AdvertisesFtms, short Rssi)
{
  /// <summary>
  /// Gets the text shown in the device list: the name followed by the Bluetooth address, which tells apart
  /// several devices with the same name.
  /// </summary>
  public string DisplayName
  {
    get
    {
      string name = string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name;
      string ftms = AdvertisesFtms ? "  FTMS" : string.Empty;
      return $"{name}  [{FormatAddress(Address)}]  {Rssi} dBm{ftms}";
    }
  }

  /// <summary>
  /// Formats a 48-bit Bluetooth address as <c>AA:BB:CC:DD:EE:FF</c>.
  /// </summary>
  /// <param name="address">The address in the lower 48 bits.</param>
  /// <returns>The colon-separated hexadecimal address.</returns>
  public static string FormatAddress(ulong address)
  {
    string hex = address.ToString("X12", System.Globalization.CultureInfo.InvariantCulture);
    return string.Join(":", Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2)));
  }
}
