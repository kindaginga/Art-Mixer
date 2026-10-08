param(
    [string] $Version = "1.0.0",
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

# Converter folder: the published exe with its vanilla catalog and the Read Me.
$conv = Join-Path $stage "converter"
Copy-Item (Join-Path $root "mod\Data\ADT\vanilla-slots.json") $conv
Copy-Item (Join-Path $root "mod\Art Distribution Tool - Read Me.txt") $conv
Get-ChildItem $conv -Filter "*.pdb" | Remove-Item

# Notices for the third-party code built into the plugin and the converter, all MIT: each component's copyright lines, then the
# license text once. MIT asks for its notice in every copy; fmt's license exempts compiled code and Boost's exempts machine code.
$utf8 = New-Object System.Text.UTF8Encoding($false)
$share = Join-Path $root "plugin\build\vs2026\vcpkg_installed\x64-windows-static-md\share"
$components = @(
    @{ Name = "CommonLibF4"; In = "ArtDistributionTool.dll"; File = Join-Path $root "plugin\external\CommonLibF4RD\LICENSE" },
    @{ Name = "nlohmann-json"; In = "ArtDistributionTool.dll"; File = Join-Path $share "nlohmann-json\copyright" },
    @{ Name = "spdlog"; In = "ArtDistributionTool.dll"; File = Join-Path $share "spdlog\copyright" },
    @{ Name = "rsm-mmio"; In = "ArtDistributionTool.dll"; File = Join-Path $share "rsm-mmio\copyright" },
    @{ Name = "zydis"; In = "ArtDistributionTool.dll"; File = Join-Path $share "zydis\copyright" },
    @{ Name = "zycore"; In = "ArtDistributionTool.dll"; File = Join-Path $share "zycore\copyright" },
    @{ Name = "libdeflate"; In = "ArtDistributionTool.dll"; File = Join-Path $share "libdeflate\copyright" },
    @{ Name = "MaterialLib"; In = "Art Distribution Tool.exe"; File = Join-Path $root "converter\external\MaterialLib\LICENSE" }
)
$notices = New-Object System.Collections.Generic.List[string]
$notices.Add("Third-party code in Art Distribution Tool")
$notices.Add("")
$notices.Add("Every component below is under the MIT License; its text follows the list.")
foreach ($in in ($components | ForEach-Object { $_.In } | Select-Object -Unique)) {
    $notices.Add("")
    $notices.Add("In $in" + ":")
    foreach ($component in $components | Where-Object { $_.In -eq $in }) {
        $lines = [IO.File]::ReadAllLines($component.File, $utf8)
        $holders = @($lines | Where-Object { $_ -match "^Copyright" } | ForEach-Object { $_.Trim() })
        if ($holders.Count -eq 0) { throw "no copyright line in $($component.File)" }
        $notices.Add(("  {0,-14} {1}" -f $component.Name, ($holders -join "; ")))
    }
}
$mit = [IO.File]::ReadAllLines($components[0].File, $utf8)
$permission = [Array]::IndexOf($mit, ($mit | Where-Object { $_ -like "Permission is hereby granted*" } | Select-Object -First 1))
if ($permission -lt 0) { throw "no MIT permission notice in $($components[0].File)" }
$notices.Add("")
$notices.Add("MIT License")
$notices.Add("")
$notices.AddRange([string[]]($mit[$permission..($mit.Count - 1)] | ForEach-Object { $_.TrimEnd() }))
while ($notices[$notices.Count - 1] -eq "") { $notices.RemoveAt($notices.Count - 1) }
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

Get-ChildItem $dist -Filter "*.zip" | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
