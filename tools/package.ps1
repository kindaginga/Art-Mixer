param(
    [string] $Version = "0.1.0",
    [switch] $SkipBuild
)
# Builds every component and writes the two release archives into dist.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"
$stage = Join-Path $dist "stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $dist, $stage | Out-Null

if (-not $SkipBuild) {
    Push-Location (Join-Path $root "plugin")
    cmake --build --preset vs2026-release | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "plugin build failed" }
    Pop-Location
    Push-Location (Join-Path $root "tools\BuildEsl")
    dotnet run -c Release -- (Join-Path $root "mod\Data\GalleryShuffle.esl") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ESL build failed" }
    Pop-Location
    Push-Location (Join-Path $root "converter\GShufConvert")
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $stage "converter") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "converter publish failed" }
    Pop-Location
}

# Core mod (FOMOD)
$core = Join-Path $stage "core"
New-Item -ItemType Directory -Force (Join-Path $core "Data\F4SE\Plugins"), (Join-Path $core "fomod") | Out-Null
Copy-Item (Join-Path $root "plugin\build\vs2026\Release\GalleryShuffle.dll") (Join-Path $core "Data\F4SE\Plugins\")
Copy-Item (Join-Path $root "mod\Data\GalleryShuffle.esl") (Join-Path $core "Data\")
Copy-Item (Join-Path $root "mod\Data\Scripts") (Join-Path $core "Data\Scripts") -Recurse
Copy-Item (Join-Path $root "mod\Data\MCM") (Join-Path $core "Data\MCM") -Recurse
Copy-Item (Join-Path $root "mod\Data\GShuf") (Join-Path $core "Data\GShuf") -Recurse
Copy-Item (Join-Path $root "mod\fomod\*") (Join-Path $core "fomod\")
Copy-Item (Join-Path $root "mod\Gallery Shuffle - Read Me.txt") $core
$coreZip = Join-Path $dist "GalleryShuffle-$Version.zip"
if (Test-Path $coreZip) { Remove-Item $coreZip }
Compress-Archive -Path (Join-Path $core "*") -DestinationPath $coreZip

# Converter
$conv = Join-Path $stage "converter"
Copy-Item (Join-Path $root "mod\Data\GShuf\vanilla-slots.json") $conv
Copy-Item (Join-Path $root "mod\Gallery Shuffle - Read Me.txt") $conv
Copy-Item (Join-Path $root "converter\external\MaterialLib\LICENSE") (Join-Path $conv "LICENSE-MaterialLib.txt")
Get-ChildItem $conv -Filter "*.pdb" | Remove-Item
$convZip = Join-Path $dist "GalleryShuffleConverter-$Version.zip"
if (Test-Path $convZip) { Remove-Item $convZip }
Compress-Archive -Path (Join-Path $conv "*") -DestinationPath $convZip

Get-ChildItem $dist -Filter "*.zip" | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
