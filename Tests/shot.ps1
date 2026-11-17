param(
    [string]$Exe = "$PSScriptRoot\..\dist\QJAR工具.exe",
    [string]$Out = "$PSScriptRoot\..\build\shot.png",
    [int]$Wait = 1200,
    [string]$Arguments = ''
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class Win {
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

[Win]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

if ($Arguments) { $process = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru }
else { $process = Start-Process -FilePath $Exe -PassThru }

$handle = [IntPtr]::Zero
for ($i = 0; $i -lt 100; $i++) {
    Start-Sleep -Milliseconds 100
    $process.Refresh()
    if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $process.MainWindowHandle; break }
}
if ($handle -eq [IntPtr]::Zero) { Write-Error '窗口未出现'; exit 1 }
Start-Sleep -Milliseconds $Wait

$rect = New-Object Win+RECT
[Win]::GetClientRect($handle, [ref]$rect) | Out-Null
$width = $rect.R - $rect.L
$height = $rect.B - $rect.T
$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
$printed = [Win]::PrintWindow($handle, $hdc, 2)
$graphics.ReleaseHdc($hdc)
if (-not $printed) {
    $graphics.Clear([System.Drawing.Color]::White)
}
$graphics.Dispose()
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

$process.CloseMainWindow() | Out-Null
Start-Sleep -Milliseconds 300
if (-not $process.HasExited) { $process.Kill() }
"$Out ${width}x${height} printed=$printed"