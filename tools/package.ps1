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

# Notices for the third-party code built into the plugin and the converter: MIT asks for its notice in every copy; fmt's license
# exempts compiled code, Boost's exempts machine code, and BCnEncoder.NET is taken under the Unlicense.
$utf8 = New-Object System.Text.UTF8Encoding($false)
$share = Join-Path $root "plugin\build\vs2026\vcpkg_installed\x64-windows-static-md\share"
$csproj = [IO.File]::ReadAllText((Join-Path $root "converter\GShufConvert\GShufConvert.csproj"))
$sharpVersion = [regex]::Match($csproj, 'Include="SharpCompress" Version="([^"]+)"').Groups[1].Value
$nuspec = [IO.File]::ReadAllText((Join-Path $env:USERPROFILE ".nuget\packages\sharpcompress\$sharpVersion\sharpcompress.nuspec"))
$sharpCopyright = [regex]::Match($nuspec, '<copyright>([^<]+)</copyright>').Groups[1].Value
if (-not $sharpCopyright) { throw "SharpCompress $sharpVersion names no copyright holder" }
$mitBody = @"
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"@
$notices = New-Object System.Collections.Generic.List[string]
$notices.Add("Third-party code in Gallery Shuffle")
function Add-Notice([string] $Title, [string] $Text) {
    $script:notices.Add(""); $script:notices.Add("=" * 72); $script:notices.Add($Title); $script:notices.Add("=" * 72); $script:notices.Add($Text.Trim())
}
Add-Notice "CommonLibF4 (in GalleryShuffle.dll)" ([IO.File]::ReadAllText((Join-Path $root "plugin\external\CommonLibF4RD\LICENSE")))
foreach ($package in 'nlohmann-json', 'spdlog', 'rsm-mmio', 'zydis', 'zycore') {
    Add-Notice "$package (in GalleryShuffle.dll)" ([IO.File]::ReadAllText((Join-Path $share "$package\copyright")))
}
Add-Notice "MaterialLib (in the converter)" ([IO.File]::ReadAllText((Join-Path $root "converter\external\MaterialLib\LICENSE")))
Add-Notice "SharpCompress $sharpVersion (in the converter)" ("MIT License`n`n$sharpCopyright`n`n" + $mitBody)
Add-Notice "BCnEncoder.NET (in the converter)" "https://github.com/Nominom/BCnEncoder.NET, offered under MIT or the Unlicense; used here under the Unlicense."
[IO.File]::WriteAllText((Join-Path $conv "THIRD-PARTY-NOTICES.txt"), (($notices -join "`r`n") + "`r`n"), $utf8)

# Core mod (FOMOD), with the converter inside as a tool (Tools\GalleryShuffle) so managers can launch it.
New-Item -ItemType Directory -Force (Join-Path $core "Data\F4SE\Plugins"), (Join-Path $core "fomod"), (Join-Path $core "Data\Tools\GalleryShuffle") | Out-Null
Copy-Item (Join-Path $conv "*") (Join-Path $core "Data\Tools\GalleryShuffle\") -Recurse
Copy-Item (Join-Path $root "plugin\build\vs2026\Release\GalleryShuffle.dll") (Join-Path $core "Data\F4SE\Plugins\")
Copy-Item (Join-Path $root "mod\Data\GalleryShuffle.esl") (Join-Path $core "Data\")
Copy-Item (Join-Path $root "mod\Data\Scripts") (Join-Path $core "Data\Scripts") -Recurse
Copy-Item (Join-Path $root "mod\Data\MCM") (Join-Path $core "Data\MCM") -Recurse
Copy-Item (Join-Path $root "mod\Data\GShuf") (Join-Path $core "Data\GShuf") -Recurse
Copy-Item (Join-Path $root "mod\fomod\*") (Join-Path $core "fomod\")
Copy-Item (Join-Path $root "mod\Gallery Shuffle - Read Me.txt") $core
Copy-Item (Join-Path $conv "THIRD-PARTY-NOTICES.txt") $core
Write-Zip $core (Join-Path $dist "GalleryShuffle-$Version.zip")

# Standalone converter
Write-Zip $conv (Join-Path $dist "GalleryShuffleConverter-$Version.zip")

Get-ChildItem $dist -Filter "*.zip" | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
