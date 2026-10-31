$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$node = "C:\Program Files\nodejs"
if (Test-Path $node) { $env:Path = "$node;$env:Path" }

$publish = Join-Path $root "artifacts\win-x64"
$icon = Join-Path $root "host\Assets\brb.ico"
New-Item -ItemType Directory -Force -Path (Split-Path $icon) | Out-Null
if (-not (Test-Path $icon)) {
    Add-Type -AssemblyName System.Drawing
    $size = 64
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $graphics = [System.Drawing.Graphics]::FromImage($bmp)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $gold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 170, 110))
    $graphics.FillEllipse($gold, 2, 2, 60, 60)
    $font = New-Object System.Drawing.Font "Segoe UI", 32, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(1, 10, 19))
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $graphics.DrawString("B", $font, $ink, (New-Object System.Drawing.RectangleF 0, 2, $size, $size), $format)
    $graphics.Dispose()
    $drawn = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
    $stream = [System.IO.File]::Create($icon)
    $drawn.Save($stream)
    $stream.Close()
    $bmp.Dispose()
}

Push-Location (Join-Path $root "web")
npm run build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Pop-Location

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root "host\Host.csproj") -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$phone = Join-Path $publish "phone"
New-Item -ItemType Directory -Force -Path $phone | Out-Null
Copy-Item (Join-Path $root "web\dist\*") $phone -Recurse -Force

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host "The app is in artifacts\win-x64. Install Inno Setup 6 to build BRB-Setup.exe."
    exit 0
}

& $iscc (Join-Path $root "installer\BRB.iss")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force -Path (Join-Path $root "site") | Out-Null
Copy-Item (Join-Path $root "artifacts\BRB-Setup.exe") (Join-Path $root "site\BRB-Setup.exe") -Force
Write-Host "Upload the site folder. The installer is site\BRB-Setup.exe."
