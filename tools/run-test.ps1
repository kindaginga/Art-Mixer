param(
    [Parameter(Mandatory)] [string] $Instance,
    [string] $Profile = "Default",
    [Parameter(Mandatory)] [string] $GameRoot,
    [string] $PluginLog = "GalleryShuffle.log",
    [int] $TimeoutSeconds = 300,
    [int] $CaptureEverySeconds = 0
)
# Launches F4SE through MO2 for one instance via the Windows shell, waits for the game to exit, and reports its output.
$mo2 = "C:\Modding\MO2\ModOrganizer.exe"
$docs = "$env:USERPROFILE\Documents\My Games\Fallout4\F4SE"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

function Capture-Desktop([string] $path) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

foreach ($p in Get-Process ModOrganizer -ErrorAction SilentlyContinue) { $null = $p.CloseMainWindow(); $p.WaitForExit(15000) | Out-Null; if (-not $p.HasExited) { $p.Kill() } }
if (Get-Process Fallout4 -ErrorAction SilentlyContinue) { throw "a Fallout4.exe is already running" }

$before = @(Get-ChildItem $GameRoot -Filter "ScreenShot*.png" -ErrorAction SilentlyContinue | ForEach-Object Name)
$crashBefore = @(Get-ChildItem $docs -Filter "crash-*.log" -ErrorAction SilentlyContinue | ForEach-Object Name)
Get-ChildItem $GameRoot -Filter "gshuf-capture-*.png" -ErrorAction SilentlyContinue | Remove-Item -Force

$cmd = Join-Path $PSScriptRoot "launch.cmd"
@("@echo off", "cd /d C:\Modding\MO2", "start `"`" `"$mo2`" -i `"$Instance`" -p $Profile run -e F4SE") | Set-Content $cmd -Encoding ascii
$sw = [Diagnostics.Stopwatch]::StartNew()
Start-Process -FilePath "$env:WINDIR\explorer.exe" -ArgumentList "`"$cmd`"" | Out-Null
$game = $null
while ($sw.Elapsed.TotalSeconds -lt 90 -and -not $game) { Start-Sleep -Seconds 2; $game = Get-Process Fallout4 -ErrorAction SilentlyContinue }
if (-not $game) {
    Get-Process ModOrganizer -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    "game did not start within 90 s"
    Get-Content "$env:LOCALAPPDATA\ModOrganizer\$Instance\logs\mo_interface.log" -Tail 12 | ForEach-Object { $_.Substring(0, [Math]::Min(200, $_.Length)) }
    return
}
$started = $sw.Elapsed.TotalSeconds
$nextCapture = $started + $CaptureEverySeconds; $captureIndex = 0
while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds -and (Get-Process Fallout4 -ErrorAction SilentlyContinue)) {
    Start-Sleep -Seconds 3
    if ($CaptureEverySeconds -gt 0 -and $sw.Elapsed.TotalSeconds -ge $nextCapture) {
        try { Capture-Desktop (Join-Path $GameRoot ("gshuf-capture-{0}.png" -f $captureIndex)) } catch {}
        $captureIndex++; $nextCapture += $CaptureEverySeconds
    }
}
$timedOut = [bool](Get-Process Fallout4 -ErrorAction SilentlyContinue)
if ($timedOut) {
    try { Capture-Desktop (Join-Path $GameRoot "gshuf-capture-timeout.png") } catch {}
    Stop-Process -Name Fallout4 -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 3
foreach ($p in Get-Process ModOrganizer -ErrorAction SilentlyContinue) { $null = $p.CloseMainWindow(); $p.WaitForExit(15000) | Out-Null; if (-not $p.HasExited) { $p.Kill() } }

"started after $([int]$started)s, ran $([int]($sw.Elapsed.TotalSeconds - $started))s, timedOut=$timedOut"
"new screenshots: " + ((Get-ChildItem $GameRoot -Filter "ScreenShot*.png" -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Name } | ForEach-Object FullName) -join '; ')
"captures: " + ((Get-ChildItem $GameRoot -Filter "gshuf-capture-*.png" -ErrorAction SilentlyContinue | ForEach-Object FullName) -join '; ')
"new crash logs: " + ((Get-ChildItem $docs -Filter "crash-*.log" -ErrorAction SilentlyContinue | Where-Object { $crashBefore -notcontains $_.Name } | ForEach-Object FullName) -join '; ')
"--- plugin log:"
Get-Content "$docs\$PluginLog" -ErrorAction SilentlyContinue | Select-Object -First 80
