param(
    [string] $Version = "0.1.0",
    [switch] $SkipBuild
)
# Builds every component and writes the two release archives into dist.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"
$stage = Join-Path $dist "stage"
$core = Join-Path $stage "core"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# Writes a zip of the folder's contents with forward-slash entry names.
function Write-Zip([string] $Folder, [string] $Destination) {
    if (Test-Path $Destination) { Remove-Item $Destination }
    $base = (Resolve-Path $Folder).Path.TrimEnd('\') + '\'
    $zip = [System.IO.Compression.ZipFile]::Open($Destination, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem $Folder -Recurse -File | Sort-Object FullName | ForEach-Object {
            $name = $_.FullName.Substring($base.Length).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally {
        $zip.Dispose()
    }
}

if (-not $SkipBuild -and (Test-Path $stage)) { Remove-Item $stage -Recurse -Force }
if (Test-Path $core) { Remove-Item $core -Recurse -Force }
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

# Converter folder: the published exe with its vanilla catalog, the Read Me and the MaterialLib license.
$conv = Join-Path $stage "converter"
Copy-Item (Join-Path $root "mod\Data\GShuf\vanilla-slots.json") $conv
Copy-Item (Join-Path $root "mod\Gallery Shuffle - Read Me.txt") $conv
Copy-Item (Join-Path $root "converter\external\MaterialLib\LICENSE") (Join-Path $conv "LICENSE-MaterialLib.txt")
Get-ChildItem $conv -Filter "*.pdb" | Remove-Item

# Core mod (FOMOD), with the converter inside as a tool (Tools\GalleryShuffle) so managers can launch it.
New-Item -ItemType Directory -Force (Join-Path $core "Data\F4SE\Plugins"), (Join-Path $core "fomod"), (Join-Path $core "Data\Tools\GalleryShuffle") | Out-Null
Copy-Item (Join-Path $conv "*") (Join-Path $core "Data\Tools\GalleryShuffle\") -Recurse
Copy-Item (Join-Path $root "plugin\build\vs2026\Release\GalleryShuffle.dll") (Join-Path $core "Data\F4SE\Plugins\")
Copy-Item (Join-Path $root "mod\Data\GalleryShuffle.esl") (Join-Path $core "Data\")
Copy-Item (Join-Path $root "mod\Data\Scripts") (Join-Path $core "Data\Scripts") -Recurse
Copy-Item (Join-Path $root "mod\Data\MCM") (Join-Path $core "Data\MCM") -Recurse
Copy-Item (Join-Path $root "mod\Data\GShuf") (Join-Path $core "Data\GShuf") -Recurse
Copy-Item (Join-Path $root "mod\Data\Interface") (Join-Path $core "Data\Interface") -Recurse
Copy-Item (Join-Path $root "mod\fomod\*") (Join-Path $core "fomod\")
Copy-Item (Join-Path $root "mod\Gallery Shuffle - Read Me.txt") $core
Write-Zip $core (Join-Path $dist "GalleryShuffle-$Version.zip")

# Standalone converter
Write-Zip $conv (Join-Path $dist "GalleryShuffleConverter-$Version.zip")

Get-ChildItem $dist -Filter "*.zip" | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
