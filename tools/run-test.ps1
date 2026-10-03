param(
    [Parameter(Mandatory)] [string] $Instance,
    [string] $Profile = "Default",
    [Parameter(Mandatory)] [string] $GameRoot,
    [string] $PluginLog = "ArtDistributionTool.log",
    [int] $TimeoutSeconds = 300,
    [int] $CaptureEverySeconds = 0
)
# Launches F4SE through MO2 for one instance via the Windows shell, waits for the game to exit, reports the harness results and exits 1 when the run failed.
$mo2 = "C:\Modding\MO2\ModOrganizer.exe"
$docs = "$env:USERPROFILE\Documents\My Games\Fallout4\F4SE"
$harnessIni = "$env:LOCALAPPDATA\ModOrganizer\$Instance\mods\Art Distribution Tool\F4SE\Plugins\ArtDistributionTool.ini"
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

# The MO2 instance a running ModOrganizer.exe serves, from "-i <name>" or "moshortcut://<name>:"; empty when not named.
function Get-Mo2Instance([string] $commandLine) {
    if ($commandLine -match '-i\s+"([^"]+)"') { return $Matches[1] }
    if ($commandLine -match '-i\s+(\S+)') { return $Matches[1] }
    if ($commandLine -match 'moshortcut://([^:]*):') { return $Matches[1] }
    return ""
}

# Closes the test instance's MO2; any other MO2 (the player's own instances) is left alone and stops the run.
function Close-TestMo2 {
    foreach ($proc in Get-CimInstance Win32_Process -Filter "Name='ModOrganizer.exe'") {
        $owner = Get-Mo2Instance $proc.CommandLine
        if ($owner -ne $Instance) { throw "Mod Organizer is running for '$(if ($owner) { $owner } else { 'an unnamed instance' })' (process $($proc.ProcessId)); close it before a test run" }
        $p = Get-Process -Id $proc.ProcessId -ErrorAction SilentlyContinue
        if ($p) { $null = $p.CloseMainWindow(); $p.WaitForExit(15000) | Out-Null; if (-not $p.HasExited) { $p.Kill() } }
    }
}

# Reads bEnabled and sMode from the [AutoTest] section the way AutoTest.cpp Configure reads them.
function Read-Harness([string] $path) {
    $harness = @{ Enabled = $false; Mode = "" }
    if (-not (Test-Path $path)) { return $harness }
    $inSection = $false
    foreach ($line in Get-Content $path) {
        if ($line.Contains("[")) { $inSection = $line.ToLower().Contains("[autotest]"); continue }
        $eq = $line.IndexOf("=")
        if (-not $inSection -or $eq -lt 0) { continue }
        $key = $line.Substring(0, $eq).ToLower()
        $value = $line.Substring($eq + 1).Trim()
        if ($key.Contains("benabled")) { $harness.Enabled = $value -eq "1" }
        if ($key.Contains("smode")) { $harness.Mode = $value.ToLower() }
    }
    return $harness
}

# The last "paintings slot 1" state the mcm mode logged for a stage, or $null.
function Get-ApplyState([string[]] $lines, [string] $stage) {
    $hit = $lines | Select-String -Pattern ("autotest: " + [regex]::Escape($stage) + ": paintings slot 1 in effect (-?\d+), menu file (\S+), applied file (\S+)$") | Select-Object -Last 1
    if (-not $hit) { return $null }
    return @{ InEffect = [int]$hit.Matches[0].Groups[1].Value; Applied = $hit.Matches[0].Groups[3].Value }
}

$harness = Read-Harness $harnessIni
if (-not $harness.Enabled) { throw "no [AutoTest] bEnabled=1 in $harnessIni; run rt5.py configure first" }
if (@("", "sliders", "mcm", "applynow", "loadscreens", "mainmenu") -notcontains $harness.Mode) { throw "unknown sMode '$($harness.Mode)' in $harnessIni" }
$modeName = if ($harness.Mode) { $harness.Mode } else { "place" }

Close-TestMo2
if (Get-Process Fallout4 -ErrorAction SilentlyContinue) { throw "a Fallout4.exe is already running" }

# The game saves its window position on exit; a minimized exit stores -32000,-32000, which lands the next window on a secondary monitor.
$custom = "$env:LOCALAPPDATA\ModOrganizer\$Instance\profiles\$Profile\Fallout4Custom.ini"
$addedAlwaysActive = $false
$wasReadOnly = $false
if (Test-Path $custom) {
    # A profile can keep the file read-only so the game cannot store a minimized window position; the run unlocks it until the end.
    $wasReadOnly = (Get-Item $custom).IsReadOnly
    if ($wasReadOnly) { (Get-Item $custom).IsReadOnly = $false }
    $ini = Get-Content $custom -Raw
    $fixed = [regex]::Replace($ini, "(?m)^iLocation X=[^\r\n]*", "iLocation X=0")
    $fixed = [regex]::Replace($fixed, "(?m)^iLocation Y=[^\r\n]*", "iLocation Y=0")
    if ($fixed -ne $ini) { "window position reset to the primary monitor" }
    # Keeps the game running without focus for this run only; the finally block at the end takes the line out again.
    if ($fixed -notmatch "(?m)^bAlwaysActive=") {
        $nl = if ($fixed -match "\r\n") { "`r`n" } else { "`n" }
        if ($fixed -match "(?m)^\[General\]") { $fixed = [regex]::Replace($fixed, "(?m)^\[General\]", "[General]$nl" + "bAlwaysActive=1") }
        else { $fixed = $fixed.TrimEnd() + "$nl[General]$nl" + "bAlwaysActive=1$nl" }
        $addedAlwaysActive = $true
    }
    if ($fixed -ne $ini) { Set-Content $custom $fixed -Encoding ascii -NoNewline }
}

try {
    $before = @(Get-ChildItem $GameRoot -Filter "ScreenShot*.png" -ErrorAction SilentlyContinue | ForEach-Object Name)
    $crashBefore = @(Get-ChildItem $docs -Filter "crash-*.log" -ErrorAction SilentlyContinue | ForEach-Object Name)
    Get-ChildItem $GameRoot -Filter "adt-capture-*.png" -ErrorAction SilentlyContinue | Remove-Item -Force

    $cmd = Join-Path $PSScriptRoot "launch.cmd"
    @("@echo off", "cd /d C:\Modding\MO2", "start `"`" `"$mo2`" -i `"$Instance`" -p $Profile run -e F4SE") | Set-Content $cmd -Encoding ascii
    $runStart = Get-Date
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Start-Process -FilePath "$env:WINDIR\explorer.exe" -ArgumentList "`"$cmd`"" | Out-Null
    $game = $null
    while ($sw.Elapsed.TotalSeconds -lt 90 -and -not $game) { Start-Sleep -Seconds 2; $game = Get-Process Fallout4 -ErrorAction SilentlyContinue }
    if (-not $game) {
        try { Close-TestMo2 } catch { $_.Exception.Message }
        "game did not start within 90 s"
        Get-Content "$env:LOCALAPPDATA\ModOrganizer\$Instance\logs\mo_interface.log" -Tail 12 | ForEach-Object { $_.Substring(0, [Math]::Min(200, $_.Length)) }
        "verdict: FAIL: the game did not start"
        exit 1
    }
    $started = $sw.Elapsed.TotalSeconds
    $nextCapture = $started + $CaptureEverySeconds; $captureIndex = 0
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds -and (Get-Process Fallout4 -ErrorAction SilentlyContinue)) {
        Start-Sleep -Seconds 3
        if ($CaptureEverySeconds -gt 0 -and $sw.Elapsed.TotalSeconds -ge $nextCapture) {
            try { Capture-Desktop (Join-Path $GameRoot ("adt-capture-{0}.png" -f $captureIndex)) } catch {}
            $captureIndex++; $nextCapture += $CaptureEverySeconds
        }
    }
    $timedOut = [bool](Get-Process Fallout4 -ErrorAction SilentlyContinue)
    if ($timedOut) { Stop-Process -Name Fallout4 -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 3
    Close-TestMo2

    $failures = New-Object System.Collections.Generic.List[string]
    $newShots = @(Get-ChildItem $GameRoot -Filter "ScreenShot*.png" -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Name } | ForEach-Object FullName)
    $newCrashes = @(Get-ChildItem $docs -Filter "crash-*.log" -ErrorAction SilentlyContinue | Where-Object { $crashBefore -notcontains $_.Name } | ForEach-Object FullName)
    "started after $([int]$started)s, ran $([int]($sw.Elapsed.TotalSeconds - $started))s, timedOut=$timedOut, mode $modeName"
    "new screenshots: " + ($newShots -join '; ')
    "captures: " + ((Get-ChildItem $GameRoot -Filter "adt-capture-*.png" -ErrorAction SilentlyContinue | ForEach-Object FullName) -join '; ')
    "new crash logs: " + ($newCrashes -join '; ')
    if ($timedOut) { $failures.Add("the game was still running after $TimeoutSeconds s, so the test did not finish") }
    if ($newCrashes.Count -gt 0) { $failures.Add("$($newCrashes.Count) new crash logs") }

    # A warning in MCM's own log means MCM ignored some of the mod's settings.
    $mcmLog = "$docs\MCM.log"
    if ((Test-Path $mcmLog) -and (Get-Item $mcmLog).LastWriteTime -ge $runStart) {
        $mcmWarnings = @(Get-Content $mcmLog | Where-Object { $_ -match "Warning|Error" })
        "MCM log: " + $(if ($mcmWarnings.Count -gt 0) { "FAIL " + ($mcmWarnings -join "; ") } else { "no warnings" })
        if ($mcmWarnings.Count -gt 0) { $failures.Add("MCM.log has $($mcmWarnings.Count) warnings") }
    } else {
        $failures.Add("MCM.log was not written in this run")
    }

    $logPath = "$docs\$PluginLog"
    $log = @()
    if ((Test-Path $logPath) -and (Get-Item $logPath).LastWriteTime -ge $runStart) { $log = @(Get-Content $logPath) }
    else { $failures.Add("$PluginLog was not written in this run") }

    # Result lines of every harness mode and the settings files the plugin read.
    $resultPattern = 'autotest: (.*\b(PASS|FAIL)\b|.*checks passed|.*in effect|.*showed (a|no) prompt|.*: models seen|at main menu |sliders: section |menu action |loading screen test, load |apply all test, load |apply-now test, load |name check: )|\] settings: applied '
    $problemPattern = '^\[[^\]]+\] \[(warning|error|critical)\] '
    "--- results from $logPath ($($log.Count) lines):"
    $log | Where-Object { $_ -match $resultPattern -and $_ -notmatch $problemPattern }
    $problems = @($log | Where-Object { $_ -match $problemPattern })
    "--- warnings and errors: $($problems.Count)"
    $problems | Select-Object -First 60
    if ($problems.Count -gt 60) { "... $($problems.Count - 60) more in $logPath" }

    $errors = @($problems | Where-Object { $_ -match '^\[[^\]]+\] \[(error|critical)\] ' })
    if ($errors.Count -gt 0) { $failures.Add("the plugin logged $($errors.Count) errors") }
    $harnessWarnings = @($problems | Where-Object { $_ -match '^\[[^\]]+\] \[warning\] autotest: ' })
    if ($harnessWarnings.Count -gt 0) { $failures.Add("the harness logged $($harnessWarnings.Count) warnings") }
    if ($log.Count -gt 0 -and -not ($log -match 'autotest: ')) { $failures.Add("the plugin log has no autotest lines, so the harness never ran") }
    $failed = @($log | Where-Object { $_ -match 'autotest: .*\bFAIL\b' })
    if ($failed.Count -gt 0) { $failures.Add("$($failed.Count) FAIL lines") }
    if ($log.Count -gt 0 -and -not ($log -match 'autotest: at main menu true$')) { $failures.Add("the main menu never came up") }

    $passed = ""
    switch ($harness.Mode) {
        "sliders" {
            $summary = $log | Select-String -Pattern 'autotest: sliders: (\d+) of (\d+) checks passed' | Select-Object -Last 1
            if (-not $summary) { $failures.Add("no sliders summary line") }
            elseif ($summary.Matches[0].Groups[1].Value -ne $summary.Matches[0].Groups[2].Value) { $failures.Add("$($summary.Matches[0].Groups[1].Value) of $($summary.Matches[0].Groups[2].Value) slider checks passed") }
            else { $passed = "$($summary.Matches[0].Groups[1].Value) of $($summary.Matches[0].Groups[2].Value) slider checks passed" }
        }
        "mcm" {
            # A change stays pending, Cancel keeps the settings in effect, a close with nothing pending asks nothing, Apply all applies and stores across the reload.
            $start = Get-ApplyState $log "start"
            $moved = Get-ApplyState $log "slider moved to 100"
            $canceled = Get-ApplyState $log "after Cancel"
            $reloaded = Get-ApplyState $log "after the reload"
            $stray = $log | Select-String -Pattern 'autotest: a close with nothing to apply showed a prompt: (true|false)$' | Select-Object -Last 1
            $failuresBefore = $failures.Count
            if (-not $start) { $failures.Add("no start state") }
            elseif ($start.InEffect -eq 100) { $failures.Add("paintings slot 1 was 100 at the start, so the test cannot show a change") }
            else {
                if (-not $moved -or $moved.InEffect -ne $start.InEffect) { $failures.Add("the slider change did not stay pending") }
                if (-not $canceled -or $canceled.InEffect -ne $start.InEffect) { $failures.Add("Cancel did not keep the settings in effect") }
            }
            if (-not $stray -or $stray.Matches[0].Groups[1].Value -ne "false") { $failures.Add("a close with nothing to apply showed a prompt, or the step did not run") }
            if (-not $reloaded -or $reloaded.InEffect -ne 100 -or $reloaded.Applied -ne "100") { $failures.Add("Apply all did not apply and store 100 across the reload") }
            if ($failures.Count -eq $failuresBefore) { $passed = "Apply all flow, 5 of 5 checks passed" }
        }
        "applynow" { if (-not ($log -match 'autotest: after apply: models seen')) { $failures.Add("the reload after the apply never finished") } }
        "loadscreens" {
            $last = $log | Select-String -Pattern 'autotest: loading screen test, load (\d+) of (\d+)' | Select-Object -Last 1
            if (-not $last -or $last.Matches[0].Groups[1].Value -ne $last.Matches[0].Groups[2].Value) { $failures.Add("not every load ran") }
        }
        "" { if (-not ($log -match 'autotest: A2 \(placed items\): models seen')) { $failures.Add("the placed items were never photographed") } }
    }

    if ($failures.Count -gt 0) {
        "verdict: FAIL: " + ($failures -join "; ")
        exit 1
    }
    if ($passed) { "verdict: PASS: $passed" }
    else { "verdict: FINISHED: mode $modeName has no automatic result checks; judge the $($newShots.Count) new screenshots" }
} finally {
    if (Test-Path $custom) {
        $ini = Get-Content $custom -Raw
        $fixed = [regex]::Replace($ini, "(?m)^iLocation X=[^\r\n]*", "iLocation X=0")
        $fixed = [regex]::Replace($fixed, "(?m)^iLocation Y=[^\r\n]*", "iLocation Y=0")
        if ($addedAlwaysActive) { $fixed = [regex]::Replace($fixed, "(?m)^bAlwaysActive=1\r?\n?", "") }        if ($fixed -ne $ini) { Set-Content $custom $fixed -Encoding ascii -NoNewline }
        if ($wasReadOnly) { (Get-Item $custom).IsReadOnly = $true }
    }
}
