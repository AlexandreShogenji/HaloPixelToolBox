param(
    [Parameter(Mandatory = $true)]
    [string]$Source72,

    [Parameter(Mandatory = $true)]
    [string]$Source300,

    [Parameter(Mandatory = $true)]
    [string]$AppAssetsDirectory,

    [Parameter(Mandatory = $true)]
    [string]$InstallerIconDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Save-ResizedPng {
    param(
        [Parameter(Mandatory = $true)] [System.Drawing.Image]$Source,
        [Parameter(Mandatory = $true)] [string]$Destination,
        [Parameter(Mandatory = $true)] [int]$CanvasWidth,
        [Parameter(Mandatory = $true)] [int]$CanvasHeight,
        [Parameter(Mandatory = $true)] [int]$ImageWidth,
        [Parameter(Mandatory = $true)] [int]$ImageHeight
    )

    $bitmap = [System.Drawing.Bitmap]::new(
        $CanvasWidth,
        $CanvasHeight,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $bitmap.SetResolution(72, 72)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

            $left = [int](($CanvasWidth - $ImageWidth) / 2)
            $top = [int](($CanvasHeight - $ImageHeight) / 2)
            $destinationRectangle = [System.Drawing.Rectangle]::new($left, $top, $ImageWidth, $ImageHeight)
            $graphics.DrawImage($Source, $destinationRectangle)
        }
        finally {
            $graphics.Dispose()
        }

        $stream = [System.IO.File]::Open(
            $Destination,
            [System.IO.FileMode]::Create,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

function Save-PngIco {
    param(
        [Parameter(Mandatory = $true)] [System.Drawing.Image]$Source,
        [Parameter(Mandatory = $true)] [string]$Destination
    )

    $iconBitmap = [System.Drawing.Bitmap]::new(
        256,
        256,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($iconBitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.DrawImage($Source, [System.Drawing.Rectangle]::new(0, 0, 256, 256))
        }
        finally {
            $graphics.Dispose()
        }

        $pngStream = [System.IO.MemoryStream]::new()
        try {
            $iconBitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
            $pngBytes = $pngStream.ToArray()

            $fileStream = [System.IO.File]::Open(
                $Destination,
                [System.IO.FileMode]::Create,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None)
            try {
                $writer = [System.IO.BinaryWriter]::new($fileStream)
                try {
                    $writer.Write([uint16]0)
                    $writer.Write([uint16]1)
                    $writer.Write([uint16]1)
                    $writer.Write([byte]0)
                    $writer.Write([byte]0)
                    $writer.Write([byte]0)
                    $writer.Write([byte]0)
                    $writer.Write([uint16]1)
                    $writer.Write([uint16]32)
                    $writer.Write([uint32]$pngBytes.Length)
                    $writer.Write([uint32]22)
                    $writer.Write($pngBytes)
                }
                finally {
                    $writer.Dispose()
                }
            }
            finally {
                $fileStream.Dispose()
            }
        }
        finally {
            $pngStream.Dispose()
        }
    }
    finally {
        $iconBitmap.Dispose()
    }
}

$source72Path = (Resolve-Path -LiteralPath $Source72).Path
$source300Path = (Resolve-Path -LiteralPath $Source300).Path
$appAssetsPath = (Resolve-Path -LiteralPath $AppAssetsDirectory).Path
$installerIconPath = (Resolve-Path -LiteralPath $InstallerIconDirectory).Path

$sourceImage = [System.Drawing.Image]::FromFile($source72Path)
try {
    Copy-Item -LiteralPath $source72Path -Destination (Join-Path $appAssetsPath 'icon.png') -Force
    Copy-Item -LiteralPath $source300Path -Destination (Join-Path $appAssetsPath 'logo-300ppi.png') -Force

    Save-ResizedPng $sourceImage (Join-Path $appAssetsPath 'LockScreenLogo.scale-200.png') 48 48 48 48
    Save-ResizedPng $sourceImage (Join-Path $appAssetsPath 'Square150x150Logo.scale-200.png') 300 300 300 300
    Save-ResizedPng $sourceImage (Join-Path $appAssetsPath 'Square44x44Logo.scale-200.png') 88 88 88 88
    Save-ResizedPng $sourceImage (Join-Path $appAssetsPath 'Square44x44Logo.targetsize-24_altform-unplated.png') 24 24 24 24
    Save-ResizedPng $sourceImage (Join-Path $appAssetsPath 'StoreLogo.png') 50 50 50 50
    Save-ResizedPng $sourceImage (Join-Path $appAssetsPath 'Wide310x150Logo.scale-200.png') 620 300 246 246
    Save-ResizedPng $sourceImage (Join-Path $appAssetsPath 'SplashScreen.scale-200.png') 1240 600 288 288
    Save-PngIco $sourceImage (Join-Path $appAssetsPath 'appicon.ico')

    Copy-Item -LiteralPath $source72Path -Destination (Join-Path $installerIconPath 'Icon.png') -Force
    Save-PngIco $sourceImage (Join-Path $installerIconPath 'Icon.ico')
}
finally {
    $sourceImage.Dispose()
}
