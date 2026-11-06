<#
.SYNOPSIS
  Renders Assets/AppIcon.xaml into the multi-size Windows icon Assets/AppIcon.ico.

.DESCRIPTION
  Loads the vector drawings AppIconImage and AppIconSmallImage from Assets/AppIcon.xaml, renders them with WPF at
  16, 20, 24, 32, 40, 48, 64 and 256 pixels (AppIconSmallImage up to 24 pixels) and writes an .ico file: 32-bit
  BMP frames below 256 pixels, a PNG frame for 256 pixels. Run it after every change to Assets/AppIcon.xaml and
  commit both files.

.PARAMETER PreviewFolder
  Optional folder that additionally receives one PNG per size, for checking the result.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/Generate-AppIcon.ps1
#>
[CmdletBinding()]
param(
  [string] $PreviewFolder
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $root 'Assets\AppIcon.xaml'
$targetPath = Join-Path $root 'Assets\AppIcon.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$smallLimit = 24

$stream = [System.IO.File]::OpenRead($sourcePath)
try
{
  $resources = [System.Windows.Markup.XamlReader]::Load($stream)
}
finally
{
  $stream.Dispose()
}

# Renders a drawing image into a non-premultiplied BGRA bitmap of the given size.
function Render-Icon([System.Windows.Media.ImageSource] $image, [int] $size)
{
  $visual = New-Object System.Windows.Media.DrawingVisual
  $context = $visual.RenderOpen()
  $context.DrawImage($image, (New-Object System.Windows.Rect 0, 0, $size, $size))
  $context.Close()
  $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
  $bitmap.Render($visual)
  return New-Object System.Windows.Media.Imaging.FormatConvertedBitmap $bitmap, ([System.Windows.Media.PixelFormats]::Bgra32), $null, 0
}

# Encodes a bitmap as PNG.
function Get-PngBytes([System.Windows.Media.Imaging.BitmapSource] $bitmap)
{
  $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
  $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
  $memory = New-Object System.IO.MemoryStream
  $encoder.Save($memory)
  return $memory.ToArray()
}

# Encodes a bitmap as an icon BMP frame: BITMAPINFOHEADER, bottom-up BGRA pixels, then the 1-bit AND mask.
function Get-BmpFrameBytes([System.Windows.Media.Imaging.BitmapSource] $bitmap, [int] $size)
{
  $stride = $size * 4
  $pixels = New-Object byte[] ($stride * $size)
  $bitmap.CopyPixels($pixels, $stride, 0)
  $maskStride = [int][Math]::Ceiling($size / 32.0) * 4
  $memory = New-Object System.IO.MemoryStream
  $writer = New-Object System.IO.BinaryWriter $memory
  $writer.Write([int]40)
  $writer.Write([int]$size)
  $writer.Write([int]($size * 2))
  $writer.Write([int16]1)
  $writer.Write([int16]32)
  $writer.Write([int]0)
  $writer.Write([int]($pixels.Length + $maskStride * $size))
  $writer.Write([int]0)
  $writer.Write([int]0)
  $writer.Write([int]0)
  $writer.Write([int]0)
  for ($y = $size - 1; $y -ge 0; $y--)
  {
    $writer.Write($pixels, $y * $stride, $stride)
  }
  for ($y = $size - 1; $y -ge 0; $y--)
  {
    $mask = New-Object byte[] $maskStride
    for ($x = 0; $x -lt $size; $x++)
    {
      if ($pixels[$y * $stride + $x * 4 + 3] -eq 0)
      {
        $mask[[int][Math]::Floor($x / 8)] = $mask[[int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8))
      }
    }
    $writer.Write($mask)
  }
  $writer.Flush()
  return $memory.ToArray()
}

$frames = @()
foreach ($size in $sizes)
{
  $key = if ($size -le $smallLimit) { 'AppIconSmallImage' } else { 'AppIconImage' }
  $bitmap = Render-Icon $resources[$key] $size
  $png = Get-PngBytes $bitmap
  if ($PreviewFolder)
  {
    New-Item -ItemType Directory -Force $PreviewFolder | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $PreviewFolder "AppIcon-$size.png"), $png)
  }
  $data = if ($size -ge 256) { $png } else { Get-BmpFrameBytes $bitmap $size }
  $frames += , @{ Size = $size; Data = $data }
}

$memory = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $memory
$writer.Write([int16]0)
$writer.Write([int16]1)
$writer.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames)
{
  $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
  $writer.Write([byte]$dimension)
  $writer.Write([byte]$dimension)
  $writer.Write([byte]0)
  $writer.Write([byte]0)
  $writer.Write([int16]1)
  $writer.Write([int16]32)
  $writer.Write([int]$frame.Data.Length)
  $writer.Write([int]$offset)
  $offset += $frame.Data.Length
}
foreach ($frame in $frames)
{
  $writer.Write([byte[]]$frame.Data)
}
$writer.Flush()
[System.IO.File]::WriteAllBytes($targetPath, $memory.ToArray())
Write-Host "Wrote $targetPath ($($frames.Count) sizes: $($sizes -join ', '))"
