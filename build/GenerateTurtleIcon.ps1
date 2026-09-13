param(
    [Parameter(Mandatory=$true)]
    [string]$OutputPath
)

Add-Type -AssemblyName System.Drawing

$dir = [System.IO.Path]::GetDirectoryName($OutputPath)
if ($dir) { [System.IO.Directory]::CreateDirectory($dir) | Out-Null }

$bmp = New-Object System.Drawing.Bitmap 256,256
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::FromArgb(15,23,30))

# Motion streaks
$cyan = [System.Drawing.Color]::FromArgb(38,198,218)
$cyan2 = [System.Drawing.Color]::FromArgb(0,229,255)
$pen1 = New-Object System.Drawing.Pen($cyan,16)
$pen1.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen1.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($pen1,22,95,83,95)
$g.DrawLine($pen1,10,125,70,125)
$g.DrawLine($pen1,30,155,84,155)

# Turtle shell
$shellBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(24,77,84))
$shellPen = New-Object System.Drawing.Pen($cyan2,10)
$g.FillEllipse($shellBrush,70,58,125,125)
$g.DrawEllipse($shellPen,70,58,125,125)

# Shell pattern
$patternPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(95,232,238),6)
$g.DrawArc($patternPen,92,80,82,82,210,120)
$g.DrawArc($patternPen,91,80,82,82,30,120)
$g.DrawLine($patternPen,133,75,133,170)
$g.DrawLine($patternPen,88,125,180,125)

# Head and limbs
$bodyBrush = New-Object System.Drawing.SolidBrush($cyan2)
$g.FillEllipse($bodyBrush,181,92,45,38)
$g.FillEllipse($bodyBrush,92,42,25,35)
$g.FillEllipse($bodyBrush,92,170,25,35)
$g.FillEllipse($bodyBrush,153,43,25,34)
$g.FillEllipse($bodyBrush,153,170,25,34)
$g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)),207,104,6,6)

# Subtle transfer arrow on shell
$arrowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White,7)
$arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::ArrowAnchor
$g.DrawLine($arrowPen,105,125,164,125)

$g.Dispose()
$pen1.Dispose(); $shellPen.Dispose(); $patternPen.Dispose(); $arrowPen.Dispose()
$shellBrush.Dispose(); $bodyBrush.Dispose()

$hIcon = $bmp.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($hIcon)
$fs = [System.IO.File]::Create($OutputPath)
$icon.Save($fs)
$fs.Dispose()
$icon.Dispose()
$bmp.Dispose()

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeIconCleanup {
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool DestroyIcon(IntPtr handle);
}
"@
[NativeIconCleanup]::DestroyIcon($hIcon) | Out-Null

Write-Host "Generated $OutputPath"
