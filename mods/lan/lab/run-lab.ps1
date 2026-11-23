# Starts the portable instances built by make-lab.ps1 in one LAN room.
#
# The game runtime accepts only --race*/--headless, plus --lan-local, --lan-id=N, --lan-peers=N,
# --lan-host, --lan-join, --lan-bind=IP and --lan-peer=IP on LAN products; anything else is rejected at
# startup (runtime/src/main.cpp). --lan-local binds each instance to 127.0.0.<id>, which is what makes
# many instances possible on one machine. A real LAN instead uses --lan-bind=<this machine's address>
# and, when the multicast group 239.255.77.75:27909 is filtered, one --lan-peer=<other machine> per
# remote machine (mods/lan/source/module/network_thread.c advertises on UDP 27900 and 27909).
#
#   .\run-lab.ps1                                   # 24 loopback instances, p01 hosts
#   .\run-lab.ps1 -Headless -Gap 0.5                # staggered automated startup
#   .\run-lab.ps1 -Instances 12 -Bind 192.168.1.10 -Peer 192.168.1.20    # machine A, ids 1-12
#   .\run-lab.ps1 -Ids 13,14,15,16,17,18,19,20,21,22,23,24 -Peers 24 -Bind 192.168.1.20 \
#       -Peer 192.168.1.10        # machine B joins: the default host id 1 is not in this list
#   .\run-lab.ps1 -FirstRun                         # create licence zero, one instance at a time
#   .\run-lab.ps1 -DryRun                           # print command lines without launching
#   .\run-lab.ps1 -Stop                             # stop every instance on this machine
#
# Each instance writes its own transcript under <instance>\UserData\Logs, so launches are not piped
# into this console; 24 processes talking at once would bury it.
param(
    [string]$LabDir = '',
    [ValidateRange(2, 128)][int]$Instances = 24,
    [int]$Peers = 0,
    [int[]]$Ids = @(),
    [ValidateRange(1, 128)][int]$HostId = 1,
    [string]$Bind = '',
    [string[]]$Peer = @(),
    [string]$SharedCacheDir = '',
    [switch]$Headless,
    [double]$Gap = 1.0,
    [switch]$FirstRun,
    [switch]$DryRun,
    [switch]$Stop
)
$ErrorActionPreference = 'Stop'
if (-not $LabDir) { $LabDir = Join-Path $PSScriptRoot 'lab' }
$LabDir = (Resolve-Path -LiteralPath $LabDir).Path
$exeName = 'WiiCompiledLAN.exe'
if (-not (Get-Variable IsWindows -ErrorAction SilentlyContinue) -or $IsWindows -eq $null) {
    if ($env:OS -ne 'Windows_NT') { $exeName = 'WiiCompiledLAN' }
}

function Get-BuildCapacity([string]$directory) {
    $inputs = Join-Path $directory 'build-inputs.txt'
    if (Test-Path -LiteralPath $inputs) {
        $found = Select-String -LiteralPath $inputs -Pattern '^guest_layout_players=([0-9]+)$' | Select-Object -First 1
        if ($found) { return [int]$found.Matches[0].Groups[1].Value }
    }
    return 12
}

$p01 = Join-Path $LabDir 'p01'
if (-not (Test-Path -LiteralPath (Join-Path $p01 $exeName))) {
    throw "No instance under $LabDir; run make-lab.ps1 first."
}
$capacity = Get-BuildCapacity $p01
if (-not $Peers) { $Peers = $capacity }
if ($Peers -gt $capacity) { throw "--lan-peers=$Peers exceeds this build's capacity of $capacity." }
if (-not $Ids) { $Ids = 1..$Instances }
if ($Ids.Count -lt 2) { throw 'A LAN room needs at least two instances.' }
foreach ($id in $Ids) {
    if ($id -lt 1 -or $id -gt $Peers) { throw "Instance id $id does not fit --lan-peers=$Peers; the runtime enforces --lan-id <= --lan-peers." }
}

if ($Stop) {
    Get-Process -Name 'WiiCompiledLAN' -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host 'Stopped every WiiCompiledLAN process on this machine.'
    return
}

$directories = @()
foreach ($id in $Ids) {
    $directory = Join-Path $LabDir ('p{0:D2}' -f $id)
    if (-not (Test-Path -LiteralPath (Join-Path $directory $exeName))) { throw "Missing instance directory $directory" }
    if (-not (Test-Path -LiteralPath (Join-Path $directory 'portable.txt'))) { throw "$directory has no portable.txt marker" }
    $directories += $directory
}

# A licence comes from the game's own first-run wizard: a headless instance walks its menus through a
# scripted pad (WIICOMPILED_TEST_PAD_FILE, five bytes: big-endian button word then two stick bytes) and
# saves slot zero into its private NAND. The pulse cadence matches mods/lan/tests/create_profile.py.
function Write-Pad([string]$path, [int]$buttons) {
    $high = [byte](($buttons -shr 8) -band 0xFF)
    $low = [byte]($buttons -band 0xFF)
    [IO.File]::WriteAllBytes($path, [byte[]]@($high, $low, 0, 0, 0))
}

if ($FirstRun) {
    foreach ($directory in $directories) {
        $save = Join-Path $directory 'UserData\NAND\title\00010004\524d4345\data\rksys.dat'
        if (Test-Path -LiteralPath $save) { Write-Host "licence ready   $directory"; continue }
        $pad = Join-Path $directory 'lan-profile-pad.bin'
        Write-Pad $pad 0
        $env:WIICOMPILED_TEST_PAD_FILE = $pad
        $process = Start-Process -FilePath (Join-Path $directory $exeName) -ArgumentList '--headless' `
            -WorkingDirectory $directory -PassThru
        Write-Host "creating licence in $directory ..."
        $started = Get-Date
        while (-not (Test-Path -LiteralPath $save)) {
            if ($process.HasExited) { throw "instance exited with $($process.ExitCode) before saving a licence; check $directory\UserData\Logs" }
            $elapsed = ((Get-Date) - $started).TotalSeconds
            if ($elapsed -gt 180) { $process.Kill(); throw 'no licence after 180 s; check UserData\Logs' }
            if ($elapsed -gt 8 -and ($elapsed % 3) -lt 0.35) { Write-Pad $pad 0x100 } else { Write-Pad $pad 0 }
            Start-Sleep -Milliseconds 100
        }
        $process.Kill()
        Remove-Item -LiteralPath $pad -Force -ErrorAction SilentlyContinue
        Write-Host "licence ready   $directory"
    }
    Remove-Item Env:WIICOMPILED_TEST_PAD_FILE -ErrorAction SilentlyContinue
    return
}

# Share one Aurora SQLite WAL pipeline cache directory across all instances in the lab
# so visible multi-instance launches reuse compiled shader metadata without duplicating it.
$previousSharedCache = $env:MKW_SHARED_CACHE_DIR
if (-not $SharedCacheDir) {
    $SharedCacheDir = if ($previousSharedCache) { $previousSharedCache } else { Join-Path $LabDir 'SharedCache' }
}
if (-not $DryRun) {
    New-Item -ItemType Directory -Path $SharedCacheDir -Force | Out-Null
    $env:MKW_SHARED_CACHE_DIR = $SharedCacheDir
}

$launched = @()
try {
for ($index = 0; $index -lt $directories.Count; $index++) {
    $directory = $directories[$index]
    $id = $Ids[$index]
    $arguments = @()
    if ($Headless) { $arguments += '--headless' }
    if ($Bind) { $arguments += "--lan-bind=$Bind" } else { $arguments += '--lan-local' }
    $arguments += "--lan-id=$id"
    $arguments += "--lan-peers=$Peers"
    foreach ($address in $Peer) { $arguments += "--lan-peer=$address" }
    if ($id -eq $HostId) { $arguments += '--lan-host' } else { $arguments += '--lan-join' }

    if ($DryRun) {
        Write-Host ('MKW_SHARED_CACHE_DIR="{0}" "{1}" {2}    (cwd: {3})' -f $SharedCacheDir, (Join-Path $directory $exeName), ($arguments -join ' '), $directory)
        continue
    }
    if ($index -gt 0 -and $Gap -gt 0) { Start-Sleep -Seconds $Gap }
    $process = Start-Process -FilePath (Join-Path $directory $exeName) -ArgumentList $arguments `
        -WorkingDirectory $directory -PassThru
    $launched += [pscustomobject]@{ Id = $id; Instance = $directory; Process = $process.Id }
}
} finally {
    if ($null -eq $previousSharedCache) {
        Remove-Item Env:MKW_SHARED_CACHE_DIR -ErrorAction SilentlyContinue
    } else {
        $env:MKW_SHARED_CACHE_DIR = $previousSharedCache
    }
}

if ($DryRun) { return }
$launched | Format-Table Id, Process, Instance -AutoSize
if ($Ids -notcontains $HostId) {
    Write-Host "Room: $($directories.Count) instances, build capacity $capacity, all joining; host id $HostId runs elsewhere."
} else {
    Write-Host "Room: $($directories.Count) instances, build capacity $capacity, host id $HostId on this machine."
}
Write-Host 'Watch room and race state without the windows: .\watch-lab.ps1'
