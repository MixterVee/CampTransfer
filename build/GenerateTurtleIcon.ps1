param(
    [Parameter(Mandatory=$true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Rebuild the polished fast-turtle artwork from its checked-in source chunks.
# Keeping the source as text avoids GitHub/checkout binary corruption while preserving
# the actual artwork the Turtle Transfer branding was based on.
$sourceDir = Join-Path $PSScriptRoot 'turtle-icon'
$sourceParts = @(
    Join-Path $sourceDir 'source128.part1.b64'
    Join-Path $sourceDir 'source128.part2.b64'
)

foreach ($part in $sourceParts) {
    if (-not (Test-Path $part)) {
        throw "Missing Turtle Transfer artwork source: $part"
    }
}

$base64 = ($sourceParts | ForEach-Object { (Get-Content $_ -Raw).Trim() }) -join ''
$sourceBytes = [Convert]::FromBase64String($base64)
if ($sourceBytes.Length -lt 20000) {
    throw "Turtle Transfer artwork source is incomplete ($($sourceBytes.Length) bytes)."
}

$dir = [System.IO.Path]::GetDirectoryName($OutputPath)
if ($dir) { [System.IO.Directory]::CreateDirectory($dir) | Out-Null }

$sourceStream = New-Object System.IO.MemoryStream(,$sourceBytes)
$sourceImage = [System.Drawing.Image]::FromStream($sourceStream)

# Windows chooses different embedded icon sizes for Explorer, taskbar, title bar and
# the notification area. Generate every useful size from the same polished artwork.
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$images = New-Object System.Collections.Generic.List[byte[]]

try {
    foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($sourceImage, 0, 0, $size, $size)
        }
        finally {
            $graphics.Dispose()
        }

        $png = New-Object System.IO.MemoryStream
        try {
            $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
            $images.Add($png.ToArray())
        }
        finally {
            $png.Dispose()
            $bitmap.Dispose()
        }
    }

    # ICO container header followed by PNG-backed icon entries. PNG-backed entries
    # retain the glossy cyan details much better than converting them to old BMP icons.
    $file = [System.IO.File]::Create($OutputPath)
    $writer = New-Object System.IO.BinaryWriter($file)
    try {
        $writer.Write([UInt16]0)                 # reserved
        $writer.Write([UInt16]1)                 # icon
        $writer.Write([UInt16]$sizes.Count)

        [UInt32]$offset = 6 + (16 * $sizes.Count)
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $size = $sizes[$i]
            $data = $images[$i]
            $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
            $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
            $writer.Write([byte]0)               # color count
            $writer.Write([byte]0)               # reserved
            $writer.Write([UInt16]1)             # planes
            $writer.Write([UInt16]32)            # bits per pixel
            $writer.Write([UInt32]$data.Length)
            $writer.Write([UInt32]$offset)
            $offset += [UInt32]$data.Length
        }

        foreach ($data in $images) {
            $writer.Write($data)
        }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }
}
finally {
    $sourceImage.Dispose()
    $sourceStream.Dispose()
}

$iconLength = (Get-Item $OutputPath).Length
if ($iconLength -lt 25000) {
    throw "Generated Turtle Transfer icon looks incomplete ($iconLength bytes)."
}

Write-Host "Generated polished Turtle Transfer icon: $OutputPath ($iconLength bytes)"
