using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SpacewalkCommander.Infrastructure;

/// <summary>
/// Gives the native title bar and frame of a window the colours of the dark application theme, so the window
/// looks like one surface. Only cosmetic: every failure is ignored and the window keeps the default title bar.
/// </summary>
/// <remarks>
/// Uses DWM window attributes. Dark mode is honoured from Windows 10 20H1; caption, text and border colours only
/// from Windows 11 (build 22000). Older systems reject the unknown attributes with an error code, which is ignored.
/// </remarks>
internal static class DarkTitleBar
{
  /// <summary><c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>: dark title bar and window buttons.</summary>
  private const int UseImmersiveDarkModeAttribute = 20;

  /// <summary><c>DWMWA_BORDER_COLOR</c>: colour of the one-pixel window frame (Windows 11).</summary>
  private const int BorderColorAttribute = 34;

  /// <summary><c>DWMWA_CAPTION_COLOR</c>: background colour of the title bar (Windows 11).</summary>
  private const int CaptionColorAttribute = 35;

  /// <summary><c>DWMWA_TEXT_COLOR</c>: colour of the title text (Windows 11).</summary>
  private const int TextColorAttribute = 36;

  /// <summary>
  /// Applies the dark title bar. Must be called once the window has a native handle, i.e. from
  /// <see cref="Window.SourceInitialized"/> or later; earlier calls do nothing.
  /// </summary>
  /// <param name="window">The window to style.</param>
  /// <param name="captionColor">Title bar background; normally the window background colour.</param>
  /// <param name="textColor">Title text colour.</param>
  /// <param name="borderColor">Colour of the window frame.</param>
  public static void Apply(Window window, Color captionColor, Color textColor, Color borderColor)
  {
    IntPtr handle = new WindowInteropHelper(window).Handle;
    if (handle == IntPtr.Zero)
    {
      return;
    }

    SetAttribute(handle, UseImmersiveDarkModeAttribute, 1);
    SetAttribute(handle, CaptionColorAttribute, ToColorRef(captionColor));
    SetAttribute(handle, TextColorAttribute, ToColorRef(textColor));
    SetAttribute(handle, BorderColorAttribute, ToColorRef(borderColor));
  }

  /// <summary>
  /// Sets one 32-bit DWM attribute. The result is ignored on purpose: an unsupported attribute only means an older
  /// Windows version, which keeps its default look.
  /// </summary>
  /// <param name="handle">Native window handle.</param>
  /// <param name="attribute">The <c>DWMWINDOWATTRIBUTE</c> value.</param>
  /// <param name="value">The attribute value.</param>
  private static void SetAttribute(IntPtr handle, int attribute, int value)
  {
    _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
  }

  /// <summary>
  /// Converts a WPF colour to a Win32 <c>COLORREF</c> (<c>0x00BBGGRR</c>); the alpha channel is dropped.
  /// </summary>
  /// <param name="color">The colour to convert.</param>
  /// <returns>The <c>COLORREF</c> value.</returns>
  private static int ToColorRef(Color color)
  {
    return color.R | (color.G << 8) | (color.B << 16);
  }

  /// <summary>
  /// Sets a Desktop Window Manager attribute of a window.
  /// </summary>
  /// <param name="hwnd">Native window handle.</param>
  /// <param name="attribute">The <c>DWMWINDOWATTRIBUTE</c> value.</param>
  /// <param name="value">The attribute value.</param>
  /// <param name="size">Size of <paramref name="value"/> in bytes.</param>
  /// <returns>An <c>HRESULT</c>; negative on failure.</returns>
  [DllImport("dwmapi.dll")]
  private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
