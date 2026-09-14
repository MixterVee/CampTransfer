param(
    [Parameter(Mandatory=$true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Use the exact approved full Turtle Transfer artwork source.
# This is the full square master (not the cropped preview/source that caused the half-icon bug).
$sourcePath = Join-Path $PSScriptRoot 'turtle-icon\source256-full.b64'
if (-not (Test-Path $sourcePath)) {
    throw "Missing approved Turtle Transfer artwork source: $sourcePath"
}

$sourceBase64 = (Get-Content $sourcePath -Raw) -replace '\s',''
$sourceBytes = [Convert]::FromBase64String($sourceBase64)
$sourceStream = New-Object System.IO.MemoryStream(,$sourceBytes)
$sourceImage = [System.Drawing.Image]::FromStream($sourceStream)

if ($sourceImage.Width -ne $sourceImage.Height -or $sourceImage.Width -lt 256) {
    throw "Approved Turtle Transfer artwork must be square and at least 256px; got $($sourceImage.Width)x$($sourceImage.Height)."
}

$dir = [System.IO.Path]::GetDirectoryName($OutputPath)
if ($dir) { [System.IO.Directory]::CreateDirectory($dir) | Out-Null }

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
        finally { $graphics.Dispose() }

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

    $file = [System.IO.File]::Create($OutputPath)
    $writer = New-Object System.IO.BinaryWriter($file)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$sizes.Count)
        [UInt32]$offset = 6 + (16 * $sizes.Count)
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $size = $sizes[$i]
            $data = $images[$i]
            $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
            $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$data.Length)
            $writer.Write([UInt32]$offset)
            $offset += [UInt32]$data.Length
        }
        foreach ($data in $images) { $writer.Write($data) }
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
if ($iconLength -lt 25000) { throw "Generated Turtle Transfer icon looks incomplete ($iconLength bytes)." }
Write-Host "Generated Turtle Transfer icon from FULL approved artwork: $OutputPath ($iconLength bytes)"
