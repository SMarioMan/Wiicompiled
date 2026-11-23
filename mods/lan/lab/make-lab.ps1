# Builds a multi-instance LAN lab from one physical copy of a WiiCompiled LAN release.
#
# The runtime has no command-line switch for its user directory: it finds the portable root by
# looking for portable.txt from the executable's directory upwards (runtime/include/runtime_config.h,
# kPortableSearchDepth = 4) and keeps Config.toml, NAND, Cache and Logs in <root>/UserData. So every
# instance needs its own portable directory, while the executable, DLLs and disc DATA stay shared:
# NTFS hard links give each instance directory its own path to the same file records, and junctions
# do the same for the read-only wii_bootstrap tree. Zero-padded folder names keep order matching --lan-id.
#
#   .\make-lab.ps1                                             # 24 instances under <release>\lab
#   .\make-lab.ps1 -Instances 12 -GridCols 4 -CellWidth 480 -CellHeight 270
#   .\make-lab.ps1 -Release E:\builds\release-lan-win64 -LabDir E:\lab -Force
#   .\make-lab.ps1 -NandTemplate E:\lab\p01\UserData\NAND      # seed a licensed profile into each
#
# LabDir must sit on the same NTFS volume as Release: hard links are only another name for the same
# file, so an instance directory costs allocation-table entries instead of 3 GB of executable and DLL
# copies, and the multi-gigabyte DATA tree (2.6 GB, 2018 files here) is read by all of them from one place.
param(
    [string]$Release = '',
    [string]$LabDir = '',
    [ValidateRange(2, 128)][int]$Instances = 24,
    [ValidateRange(1, 128)][int]$GridCols = 6,
    # Windowed mode has a floor: a logged run rewrote a 320x180 request to 640x480 (the runtime writes
    # the resolved size back into UserData\Config.toml), so one cell per instance starts at 640x480.
    [ValidateRange(160, 8192)][int]$CellWidth = 640,
    [ValidateRange(90, 8192)][int]$CellHeight = 480,
    [int]$OriginX = 0,
    [int]$OriginY = 0,
    [string]$NandTemplate = '',
    [int]$Peers = 0,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
if (-not $Release) { $Release = $PSScriptRoot }
$Release = (Resolve-Path -LiteralPath $Release).Path
foreach ($required in @('WiiCompiledLAN.exe', 'DATA', 'wii_bootstrap')) {
    if (-not (Test-Path -LiteralPath (Join-Path $Release $required))) {
        throw "Missing $required under $Release; pass -Release <folder> holding an unpacked LAN release."
    }
}
if (-not $LabDir) { $LabDir = Join-Path $Release 'lab' }

# The runtime refuses --lan-id/--lan-peers above the capacity the guest and module were built for, so
# take the capacity from the release's own provenance instead of trusting the requested count. The
# default profile is twelve players; an expanded build records guest_layout_players.
$capacity = 12
$inputs = Join-Path $Release 'build-inputs.txt'
if (Test-Path -LiteralPath $inputs) {
    $found = Select-String -LiteralPath $inputs -Pattern '^guest_layout_players=([0-9]+)$' | Select-Object -First 1
    if ($found) { $capacity = [int]$found.Matches[0].Groups[1].Value }
}
if (-not $Peers) { $Peers = $capacity }
if ($Instances -gt $capacity) {
    throw "$Release holds a $capacity-player LAN build; it cannot host $Instances instances. Rebuild it with WIICOMPILED_LAN_PLAYERS=$Instances, or pass -Instances $capacity."
}
if ($Peers -lt $Instances) {
    throw "-Peers $Peers must be at least -Instances $Instances, because the runtime rejects --lan-id above --lan-peers."
}
if ($NandTemplate) { $NandTemplate = (Resolve-Path -LiteralPath $NandTemplate).Path }

# One shared disc tree, referenced absolutely: forward slashes keep the TOML value free of escapes.
$dvdRoot = (Resolve-Path -LiteralPath (Join-Path $Release 'DATA')).Path -replace '\\', '/'

# An instance folder holds junctions into the release. Remove-Item -Recurse can follow a junction into
# its target, so unlink the reparse points first and only then delete the tree.
function Remove-LabInstances([string]$root) {
    foreach ($instance in (Get-ChildItem -LiteralPath $root -Directory)) {
        foreach ($child in (Get-ChildItem -LiteralPath $instance.FullName -Force)) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                [IO.Directory]::Delete($child.FullName)
            }
        }
        Remove-Item -LiteralPath $instance.FullName -Recurse -Force
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}

if (Test-Path -LiteralPath $LabDir) {
    if (-not $Force) { throw "$LabDir already exists; pass -Force to rebuild it." }
    Remove-LabInstances $LabDir
}

# Hard links share clusters, so the free-space delta around creation is the evidence that the instances
# really do point at one copy; a silent copy would show up here as a multi-gigabyte drop.
$labDrive = (Split-Path -Qualifier $LabDir).TrimEnd(':')
$freeBefore = (Get-PSDrive -Name $labDrive).Free
New-Item -ItemType Directory -Path $LabDir -Force | Out-Null

# Everything else in the release root is shared by hard link. Helper and installer files are not part
# of a running instance, and the lab folder must never contain a link back into itself.
$skipNames = @('portable.txt', 'VC_redist.x64.exe', 'make-lab.ps1', 'run-lab.ps1', 'watch-lab.ps1',
    'lan-lab.md')
$sharedFiles = @(Get-ChildItem -LiteralPath $Release -File | Where-Object { $skipNames -notcontains $_.Name })
$sharedDirectories = @('wii_bootstrap')

foreach ($index in 1..$Instances) {
    $name = 'p{0:D2}' -f $index
    $instance = Join-Path $LabDir $name
    New-Item -ItemType Directory -Path (Join-Path $instance 'UserData') -Force | Out-Null

    foreach ($file in $sharedFiles) {
        New-Item -ItemType HardLink -Path (Join-Path $instance $file.Name) -Target $file.FullName | Out-Null
    }
    foreach ($directory in $sharedDirectories) {
        New-Item -ItemType Junction -Path (Join-Path $instance $directory) -Target (Join-Path $Release $directory) | Out-Null
    }

    # The marker is what isolates this instance from its siblings: without it the search walks up into
    # the shared release root and every instance would share one NAND, Cache, Config and Logs tree.
    Set-Content -LiteralPath (Join-Path $instance 'portable.txt') -Value '' -Encoding Ascii -NoNewline

    $column = ($index - 1) % $GridCols
    $row = [math]::Floor(($index - 1) / $GridCols)
    $x = $OriginX + $column * $CellWidth
    $y = $OriginY + $row * $CellHeight
    $lines = @()
    $lines += '[paths]'
    $lines += ('dvd_root = "{0}"' -f $dvdRoot)
    $lines += ''
    $lines += '[video]'
    $lines += 'display_mode = "windowed"'
    $lines += ('window_x = {0}' -f $x)
    $lines += ('window_y = {0}' -f $y)
    $lines += ('window_width = {0}' -f $CellWidth)
    $lines += ('window_height = {0}' -f $CellHeight)
    Set-Content -LiteralPath (Join-Path $instance 'UserData\Config.toml') -Value ($lines -join "`n") -Encoding Ascii -NoNewline

    # Each instance keeps its own writable NAND: it holds the licence and the save, so it is copied,
    # never linked. A template only gives a new instance a running start.
    if ($NandTemplate) {
        $target = Join-Path $instance 'UserData\NAND'
        if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $NandTemplate -Destination $target -Recurse }
    }
    Write-Host ('{0}  {1}x{2} at ({3},{4})' -f $name, $CellWidth, $CellHeight, $x, $y)
}

Write-Host ''
$sharedBytes = ($sharedFiles | Measure-Object -Property Length -Sum).Sum
$eachMb = [math]::Round($sharedBytes / 1MB)
$avoidedMb = [math]::Round($sharedBytes * ($Instances - 1) / 1MB)
$freeNow = (Get-PSDrive -Name $labDrive).Free
$usedMb = [math]::Round(($freeBefore - $freeNow) / 1MB)
Write-Host ('{0} : {1} portable instances of one shared executable and DATA tree, build capacity {2}.' -f $LabDir, $Instances, $capacity)
Write-Host ('root files shared by hard link: {0} MB each; {1} instances would cost {2} MB as copies, the lab consumed {3} MB.' -f $eachMb, $Instances, $avoidedMb, $usedMb)
Write-Host 'DATA stays one tree: every instance points at it with dvd_root instead of a private copy.'
Write-Host ('Start them with:  .\run-lab.ps1 -LabDir "{0}" -Headless' -f $LabDir)
