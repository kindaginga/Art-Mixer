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
    dotnet run -c Release -- (Join-Path $root "mod\Data\ArtDistributionTool.esl") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ESL build failed" }
    Pop-Location
    Push-Location (Join-Path $root "converter\ADTConvert")
    # Framework-dependent: the exe needs the .NET 10 Desktop Runtime on the player's machine and carries no runtime of its own.
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:Version=$Version -o (Join-Path $stage "converter") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "converter publish failed" }
    Pop-Location
}

# Converter folder: the published exe with its vanilla catalog, the Read Me and the MaterialLib license.
$conv = Join-Path $stage "converter"
Copy-Item (Join-Path $root "mod\Data\ADT\vanilla-slots.json") $conv
Copy-Item (Join-Path $root "mod\Art Distribution Tool - Read Me.txt") $conv
Copy-Item (Join-Path $root "converter\external\MaterialLib\LICENSE") (Join-Path $conv "LICENSE-MaterialLib.txt")
Get-ChildItem $conv -Filter "*.pdb" | Remove-Item

# Notices for the third-party code built into the plugin and the converter: MIT asks for its notice in every copy; fmt's license
# exempts compiled code and Boost's exempts machine code.
$utf8 = New-Object System.Text.UTF8Encoding($false)
$share = Join-Path $root "plugin\build\vs2026\vcpkg_installed\x64-windows-static-md\share"
$notices = New-Object System.Collections.Generic.List[string]
$notices.Add("Third-party code in Art Distribution Tool")
function Add-Notice([string] $Title, [string] $Text) {
    $script:notices.Add(""); $script:notices.Add("=" * 72); $script:notices.Add($Title); $script:notices.Add("=" * 72); $script:notices.Add($Text.Trim())
}
Add-Notice "CommonLibF4 (in ArtDistributionTool.dll)" ([IO.File]::ReadAllText((Join-Path $root "plugin\external\CommonLibF4RD\LICENSE")))
foreach ($package in 'nlohmann-json', 'spdlog', 'rsm-mmio', 'zydis', 'zycore') {
    Add-Notice "$package (in ArtDistributionTool.dll)" ([IO.File]::ReadAllText((Join-Path $share "$package\copyright")))
}
Add-Notice "MaterialLib (in the converter)" ([IO.File]::ReadAllText((Join-Path $root "converter\external\MaterialLib\LICENSE")))
[IO.File]::WriteAllText((Join-Path $conv "THIRD-PARTY-NOTICES.txt"), (($notices -join "`r`n") + "`r`n"), $utf8)

# Core mod (FOMOD), with the converter inside as a tool (Tools\ArtDistributionTool) so managers can launch it.
New-Item -ItemType Directory -Force (Join-Path $core "Data\F4SE\Plugins"), (Join-Path $core "fomod"), (Join-Path $core "Data\Tools\ArtDistributionTool") | Out-Null
Copy-Item (Join-Path $conv "*") (Join-Path $core "Data\Tools\ArtDistributionTool\") -Recurse
Copy-Item (Join-Path $root "plugin\build\vs2026\Release\ArtDistributionTool.dll") (Join-Path $core "Data\F4SE\Plugins\")
Copy-Item (Join-Path $root "mod\Data\ArtDistributionTool.esl") (Join-Path $core "Data\")
Copy-Item (Join-Path $root "mod\Data\Scripts") (Join-Path $core "Data\Scripts") -Recurse
Copy-Item (Join-Path $root "mod\Data\MCM") (Join-Path $core "Data\MCM") -Recurse
Copy-Item (Join-Path $root "mod\Data\ADT") (Join-Path $core "Data\ADT") -Recurse
Copy-Item (Join-Path $root "mod\fomod\*") (Join-Path $core "fomod\")

# The list of the core mod's files with size and SHA-256, which the converter and the plugin check the installed mod against.
# The converter's packs replace the three replaceable files where both mods are deployed into one folder.
$data = (Resolve-Path (Join-Path $core "Data")).Path.TrimEnd('\') + '\'
$replaceable = 'ADT/vanilla-slots.json', 'MCM/Config/ArtDistributionTool/config.json', 'MCM/Config/ArtDistributionTool/settings.ini'
$entries = Get-ChildItem $data -Recurse -File | Sort-Object FullName | ForEach-Object {
    $path = $_.FullName.Substring($data.Length).Replace('\', '/')
    $entry = '    { "path": "' + $path + '", "size": ' + $_.Length + ', "sha256": "' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '"'
    if ($replaceable -contains $path) { $entry += ', "replaceable": true' }
    $entry + ' }'
}
[IO.File]::WriteAllText((Join-Path $data "ADT\core-files.json"), "{`n  `"version`": `"$Version`",`n  `"files`": [`n" + ($entries -join ",`n") + "`n  ]`n}`n", $utf8)

Write-Zip $core (Join-Path $dist "ArtDistributionTool-$Version.zip")

# Standalone converter
Write-Zip $conv (Join-Path $dist "ArtDistributionToolConverter-$Version.zip")

Get-ChildItem $dist -Filter "*.zip" | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
