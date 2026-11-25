<#
.SYNOPSIS
    Starts and automatically connects N WiiCompiled LAN instances (2..128) into one LAN lobby on Windows.

.DESCRIPTION
    Turnkey Windows launcher that:
      1. Prepares N portable instance folders via NTFS hard links (zero duplicate GBs of EXE/DLL/DATA).
      2. Seeds a license-0 profile (rksys.dat) once if needed and copies it to any unseeded instance.
      3. Configures MKW_SHARED_CACHE_DIR so all visible instances share one Aurora SQLite pipeline cache.
      4. Launches instance 1 as --lan-host and instances 2..N as --lan-join clients.
      5. Monitors live UDP telemetry (127.0.0.254:27900) until all N instances are connected in the same
         LAN room (page 0x9D, full SDK connectivity) and leaves the lobby ready to play (or stops them if
         -StopAfterReady is passed).

.EXAMPLE
    .\start-lan-lobby.ps1 -Count 4
    .\start-lan-lobby.ps1 -Count 32 -AutoDrive -SharedAudioMix -GameCubeAdapterUnlimited
    .\start-lan-lobby.ps1 -Count 32 -GridCols 8 -AutoDrive
    .\start-lan-lobby.ps1 -Count 128 -Headless -ClockDivisor 4
    .\start-lan-lobby.ps1 -Stop
#>
param(
    [Parameter(Position = 0)]
    [ValidateRange(2, 128)][int]$Count = 4,
    [string]$Release = '',
    [string]$LabDir = '',
    [string]$SharedCacheDir = '',
    [int]$Peers = 0,
    [ValidateRange(0, 128)][int]$GridCols = 0,
    [ValidateRange(0, 8192)][int]$CellWidth = 0,
    [ValidateRange(0, 8192)][int]$CellHeight = 0,
    [ValidateRange(0, 16384)][int]$ScreenWidth = 0,
    [ValidateRange(0, 16384)][int]$ScreenHeight = 0,
    [int]$OriginX = 0,
    [int]$OriginY = 0,
    [double]$Gap = 0.3,
    [ValidateRange(15, 3600)][int]$TimeoutSec = 300,
    [ValidateRange(1, 64)][int]$ClockDivisor = 1,
    [ValidateSet('', 'auto', 'd3d12', 'vulkan')][string]$GraphicsApi = '',
    [ValidateRange(0.0, 4.0)][double]$ResolutionMultiplier = 0.0,
    [switch]$AllAudio,
    [switch]$SharedAudioMix,
    [Alias('GCUnlimited')]
    [switch]$GameCubeAdapterUnlimited,
    [string]$GcuPath = 'E:\Projects\mario-kart-multiplayer\0-Global\GameCubeAdapterUnlimited.exe',
    [switch]$SingleGpu,
    [switch]$HighCountOpt,
    [ValidateRange(0, 128)][int]$KartCullCap = 0,
    [Alias('CpuDrive')]
    [switch]$AutoDrive,
    [switch]$Headless,
    [switch]$RebuildLab,
    [switch]$StopAfterReady,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'

if ($Stop) {
    $procs = @(Get-Process -Name 'WiiCompiledLAN' -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) {
        $procs | Stop-Process -Force
        Write-Host "Stopped $($procs.Count) WiiCompiledLAN process(es)."
    } else {
        Write-Host 'No running WiiCompiledLAN processes found.'
    }
    return
}

# --- 1. Locate release directory ---
function Resolve-ReleaseDir([string]$hint) {
    $candidates = @()
    if ($hint) { $candidates += $hint }
    $candidates += $PSScriptRoot
    $repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
    $candidates += (Join-Path $repoRoot 'release-lan-128-win64')
    $candidates += (Join-Path $repoRoot 'release-lan-win64')
    foreach ($dir in $candidates) {
        if ($dir -and (Test-Path -LiteralPath (Join-Path $dir 'WiiCompiledLAN.exe')) -and
            (Test-Path -LiteralPath (Join-Path $dir 'DATA')) -and
            (Test-Path -LiteralPath (Join-Path $dir 'wii_bootstrap'))) {
            return (Resolve-Path -LiteralPath $dir).Path
        }
    }
    throw "Could not find a WiiCompiled LAN Windows release (WiiCompiledLAN.exe + DATA + wii_bootstrap). Pass -Release <path>."
}

$Release = Resolve-ReleaseDir $Release
if (-not $LabDir) { $LabDir = Join-Path $Release 'lab' }

# Detect build capacity from build-inputs.txt (defaults to 12 for stock builds).
$capacity = 12
$buildInputs = Join-Path $Release 'build-inputs.txt'
if (Test-Path -LiteralPath $buildInputs) {
    $m = Select-String -LiteralPath $buildInputs -Pattern '^guest_layout_players=([0-9]+)$' | Select-Object -First 1
    if ($m) { $capacity = [int]$m.Matches[0].Groups[1].Value }
}
if (-not $Peers) { $Peers = $capacity }
if ($Count -gt $capacity) {
    throw "$Release holds a $capacity-player build; it cannot host $Count instances. Rebuild with WIICOMPILED_LAN_PLAYERS=$Count or pass -Count <= $capacity."
}
if ($Peers -lt $Count) {
    throw "-Peers $Peers must be at least -Count $Count."
}

# --- 1b. Resolve display size, grid columns, and cell dimensions automatically when omitted ---
function Get-PrimaryDisplaySize {
    if (-not ('MkwDisplayInfo' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MkwDisplayInfo {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    }
    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
    public static int[] PrimarySize() {
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(dm);
        if (EnumDisplaySettings(null, -1, ref dm) && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
            return new[] { dm.dmPelsWidth, dm.dmPelsHeight };
        return new[] { 1920, 1080 };
    }
}
'@
    }
    return [MkwDisplayInfo]::PrimarySize()
}

if ($ScreenWidth -le 0 -or $ScreenHeight -le 0) {
    $primarySize = Get-PrimaryDisplaySize
    if ($ScreenWidth -le 0) { $ScreenWidth = $primarySize[0] }
    if ($ScreenHeight -le 0) { $ScreenHeight = $primarySize[1] }
}

if ($GridCols -le 0) {
    # Pick the grid (cols x rows) that keeps cells closest to 4:3..16:9 with minimal empty slots.
    $targetAspect = 1.3333
    $bestCols = 1
    $bestScore = [double]::PositiveInfinity
    for ($r = 1; $r -le $Count; $r++) {
        $c = [int][math]::Ceiling($Count / [double]$r)
        $cellAspect = ($ScreenWidth / [double]$c) / ($ScreenHeight / [double]$r)
        $wasteRatio = ($c * $r - $Count) / [double]$Count
        $score = [math]::Abs([math]::Log($cellAspect / $targetAspect)) + 4.0 * $wasteRatio
        if ($c * $r -eq $Count) { $score -= 0.35 }
        if ($score -lt $bestScore) {
            $bestScore = $score
            $bestCols = $c
        }
    }
    $GridCols = $bestCols
}

$gridRows = [int][math]::Ceiling($Count / [double]$GridCols)
if ($CellWidth -le 0) { $CellWidth = [math]::Max(120, [int][math]::Floor($ScreenWidth / [double]$GridCols)) }
if ($CellHeight -le 0) { $CellHeight = [math]::Max(90, [int][math]::Floor($ScreenHeight / [double]$gridRows)) }
$useWidescreen = ($CellWidth / [double]$CellHeight) -gt (4.0 / 3.0 + 0.001)

# --- 2. Prepare / expand portable instance directories via NTFS hard links ---
$dvdRoot = (Resolve-Path -LiteralPath (Join-Path $Release 'DATA')).Path -replace '\\', '/'
$skipNames = @('portable.txt', 'VC_redist.x64.exe', 'make-lab.ps1', 'run-lab.ps1',
               'watch-lab.ps1', 'start-lan-lobby.ps1', 'lan-lab.md')
$sharedFiles = @(Get-ChildItem -LiteralPath $Release -File | Where-Object { $skipNames -notcontains $_.Name })

function Remove-LabDirectory([string]$root) {
    if (-not (Test-Path -LiteralPath $root)) { return }
    foreach ($dir in (Get-ChildItem -LiteralPath $root -Directory)) {
        foreach ($child in (Get-ChildItem -LiteralPath $dir.FullName -Force -ErrorAction SilentlyContinue)) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                [IO.Directory]::Delete($child.FullName)
            }
        }
        Remove-Item -LiteralPath $dir.FullName -Recurse -Force
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}

if ($RebuildLab -and (Test-Path -LiteralPath $LabDir)) {
    Write-Host "Rebuilding lab directory $LabDir ..."
    Remove-LabDirectory $LabDir
}
New-Item -ItemType Directory -Path $LabDir -Force | Out-Null

$instanceDirs = @()
for ($i = 1; $i -le $Count; $i++) {
    $name = 'p{0:D2}' -f $i
    $instDir = Join-Path $LabDir $name
    $instanceDirs += $instDir

    New-Item -ItemType Directory -Path (Join-Path $instDir 'UserData') -Force | Out-Null

    foreach ($file in $sharedFiles) {
        $dst = Join-Path $instDir $file.Name
        if (-not (Test-Path -LiteralPath $dst)) {
            New-Item -ItemType HardLink -Path $dst -Target $file.FullName | Out-Null
        }
    }
    $bootLink = Join-Path $instDir 'wii_bootstrap'
    if (-not (Test-Path -LiteralPath $bootLink)) {
        New-Item -ItemType Junction -Path $bootLink -Target (Join-Path $Release 'wii_bootstrap') | Out-Null
    }
    $portablePath = Join-Path $instDir 'portable.txt'
    if (-not (Test-Path -LiteralPath $portablePath)) {
        Set-Content -LiteralPath $portablePath -Value '' -Encoding Ascii -NoNewline
    }

    $effectiveApi = if ($GraphicsApi) { $GraphicsApi } elseif ($Count -ge 16 -and -not $Headless) { 'vulkan' } elseif (-not $Headless) { 'd3d12' } else { '' }
    $effectiveRes = if ($ResolutionMultiplier -gt 0.0) { $ResolutionMultiplier } else { 0.0 }
    $configPath = Join-Path $instDir 'UserData\Config.toml'
    if (-not $Headless -or $RebuildLab -or -not (Test-Path -LiteralPath $configPath) -or $effectiveApi -or $effectiveRes -gt 0.0) {
        $col = ($i - 1) % $GridCols
        $row = [math]::Floor(($i - 1) / $GridCols)
        $x = $OriginX + $col * $CellWidth
        $y = $OriginY + $row * $CellHeight
        $cfgLines = @(
            '[paths]'
            ('dvd_root = "{0}"' -f $dvdRoot)
            ''
            '[video]'
            'display_mode = "windowed"'
            ('widescreen = {0}' -f $(if ($useWidescreen) { 'true' } else { 'false' }))
        )
        if ($effectiveApi) { $cfgLines += ('graphics_api = "{0}"' -f $effectiveApi) }
        if ($effectiveRes -gt 0.0) { $cfgLines += ('resolution_multiplier = {0}' -f $effectiveRes.ToString('0.##', [Globalization.CultureInfo]::InvariantCulture)) }
        $cfgLines += @(
            ('window_x = {0}' -f $x)
            ('window_y = {0}' -f $y)
            ('window_width = {0}' -f $CellWidth)
            ('window_height = {0}' -f $CellHeight)
        )
        $cfg = $cfgLines -join "`n"
        Set-Content -LiteralPath $configPath -Value $cfg -Encoding Ascii -NoNewline
    }
}

# --- 3. Seed license 0 (rksys.dat with RKPD header) once and distribute to unseeded instances ---
$saveRelPath = 'UserData\NAND\title\00010004\524d4345\data\rksys.dat'

function Test-HasLicenseZero([string]$rootDir) {
    $saveFile = Join-Path $rootDir $saveRelPath
    if (-not (Test-Path -LiteralPath $saveFile)) { return $false }
    try {
        $fs = [IO.File]::OpenRead($saveFile)
        try {
            if ($fs.Length -lt 12) { return $false }
            $hdr = New-Object byte[] 12
            $null = $fs.Read($hdr, 0, 12)
            # Bytes 0x08..0x0B hold "RKPD" (0x52 0x4B 0x50 0x44) once license slot 0 is created.
            return ($hdr[8] -eq 0x52 -and $hdr[9] -eq 0x4B -and $hdr[10] -eq 0x50 -and $hdr[11] -eq 0x44)
        } finally {
            $fs.Close()
        }
    } catch {
        return $false
    }
}

$seedNand = $null
foreach ($probe in (@($instanceDirs[0], $Release) + $instanceDirs)) {
    if (Test-HasLicenseZero $probe) {
        $seedNand = Join-Path $probe 'UserData\NAND'
        break
    }
}

function Write-ScriptedPad([string]$path, [int]$buttons) {
    $hi = [byte](($buttons -shr 8) -band 0xFF)
    $lo = [byte]($buttons -band 0xFF)
    [IO.File]::WriteAllBytes($path, [byte[]]@($hi, $lo, 0, 0, 0))
}

if (-not $seedNand) {
    $seedDir = $instanceDirs[0]
    $padPath = Join-Path $seedDir 'lan-profile-pad.bin'
    Write-ScriptedPad $padPath 0
    Write-Host "No license 0 found; creating license 0 once in $seedDir ..."
    $prevPad = $env:WIICOMPILED_TEST_PAD_FILE
    $env:WIICOMPILED_TEST_PAD_FILE = $padPath
    $seedProc = Start-Process -FilePath (Join-Path $seedDir 'WiiCompiledLAN.exe') `
        -ArgumentList '--headless' -WorkingDirectory $seedDir -PassThru
    try {
        $t0 = Get-Date
        while (-not (Test-HasLicenseZero $seedDir)) {
            if ($seedProc.HasExited) {
                throw "License creation exited early with code $($seedProc.ExitCode); inspect $seedDir\UserData\Logs."
            }
            $elapsed = ((Get-Date) - $t0).TotalSeconds
            if ($elapsed -gt 180) {
                throw "Timed out waiting for license creation after 180 s; inspect $seedDir\UserData\Logs."
            }
            if ($elapsed -gt 8 -and ($elapsed % 3) -lt 0.35) {
                Write-ScriptedPad $padPath 0x0100
            } else {
                Write-ScriptedPad $padPath 0
            }
            Start-Sleep -Milliseconds 100
        }
    } finally {
        if (-not $seedProc.HasExited) { $seedProc.Kill() }
        $seedProc.WaitForExit(5000) | Out-Null
        Remove-Item -LiteralPath $padPath -Force -ErrorAction SilentlyContinue
        if ($null -eq $prevPad) { Remove-Item Env:WIICOMPILED_TEST_PAD_FILE -ErrorAction SilentlyContinue }
        else { $env:WIICOMPILED_TEST_PAD_FILE = $prevPad }
    }
    $seedNand = Join-Path $seedDir 'UserData\NAND'
    Write-Host "License 0 created and ready."
}

$seededCount = 0
foreach ($instDir in $instanceDirs) {
    if (-not (Test-HasLicenseZero $instDir)) {
        $dstNand = Join-Path $instDir 'UserData\NAND'
        if (Test-Path -LiteralPath $dstNand) { Remove-Item -LiteralPath $dstNand -Recurse -Force }
        Copy-Item -LiteralPath $seedNand -Destination $dstNand -Recurse
        $seededCount++
    }
}
if ($seededCount -gt 0) {
    Write-Host "Seeded license 0 NAND into $seededCount instance(s)."
}

# Generate a unique default Mii (RFL_DB.dat) and link License 0 (rksys.dat) for each
# instance, cycling through:
#   12 favorite colors (0..11) -> 2 genders (Male/Female) -> 3 MKW Mii sizes (Small/Medium/Large)
# with UTF-16BE name "Player N" so every player has a distinct Mii, shirt color,
# gender, weight class, and "Player #" label natively across all menus and races.
if (-not ('MkwRksysCrc' -as [type])) {
    Add-Type -TypeDefinition @'
public static class MkwRksysCrc {
    public static uint Compute(byte[] data, int len) {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < len; i++) {
            crc ^= data[i];
            for (int k = 0; k < 8; k++) {
                crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xEDB88320u : (crc >> 1);
            }
        }
        return ~crc;
    }

    public static ushort ComputeRflCrc16(byte[] data, int len) {
        uint crc = 0u;
        for (int i = 0; i < len; i++) {
            crc ^= (uint)data[i] << 8;
            for (int k = 0; k < 8; k++) {
                crc = (crc & 0x8000u) != 0 ? ((crc << 1) ^ 0x1021u) & 0xFFFFu : (crc << 1) & 0xFFFFu;
            }
        }
        return (ushort)crc;
    }
}
'@
}

# Standard Wii RFLCharData facial feature bytes (offsets 0x20..0x35, 22 bytes):
# Default Male (hair=33 brown, eye=2) and Default Female (hair=12 brown, eye=4).
$defaultMaleFace = [byte[]]@(
    0x00, 0x00, 0x42, 0x40, 0x01, 0x80, 0x28, 0xA2, 0x08, 0x8C, 0x08, 0x40,
    0x14, 0x48, 0xB8, 0x8D, 0x00, 0x8A, 0x00, 0x8A, 0x25, 0x04
)
$defaultFemaleFace = [byte[]]@(
    0x00, 0x00, 0x18, 0x40, 0x01, 0x80, 0x28, 0xA2, 0x10, 0x8C, 0x08, 0x40,
    0x14, 0x48, 0xB8, 0x8D, 0x00, 0x8A, 0x00, 0x8A, 0x25, 0x04
)
# MKW Mii size thresholds (Mii::Load 0x80521BAC): Small (weight <= 104-height),
# Medium (between), Large (weight >= 150-height).
$miiSizeTable = @(
    @{ Height = [byte]32; Weight = [byte]32 }, # Small (Light)
    @{ Height = [byte]64; Weight = [byte]64 }, # Medium
    @{ Height = [byte]96; Weight = [byte]96 }  # Large (Heavy)
)

$renamedCount = 0
$miiDbCount = 0
for ($i = 1; $i -le $Count; $i++) {
    $idx = $i - 1
    $color = $idx % 12
    $gender = [int]([math]::Floor($idx / 12)) % 2
    $sizeClass = [int]([math]::Floor($idx / 24)) % 3
    $dims = $miiSizeTable[$sizeClass]

    $desiredName = "Player $i"
    if ($desiredName.Length -gt 10) { $desiredName = "P$i" }
    $nameBytes = New-Object byte[] 20
    $encoded = [Text.Encoding]::BigEndianUnicode.GetBytes($desiredName)
    [Array]::Copy($encoded, 0, $nameBytes, 0, [Math]::Min(20, $encoded.Length))

    # Unique normal-Mii RFLCreateID (8 bytes: 4-byte Mii ID with normal bit 0x80 + 4-byte System ID).
    $miiCreateId = [byte[]]@(0x80, 0x00, 0x00, [byte]($i -band 0xFF), 0x4D, 0x4B, 0x57, 0x00)

    # 1. Build 74-byte RFLCharData and write UserData\NAND\shared2\menu\FaceLib\RFL_DB.dat
    $charData = New-Object byte[] 74
    $charData[0x00] = [byte]($gender -shl 6)
    $charData[0x01] = [byte](($color -band 0x0F) -shl 1)
    [Array]::Copy($nameBytes, 0, $charData, 0x02, 20)
    $charData[0x16] = $dims.Height
    $charData[0x17] = $dims.Weight
    [Array]::Copy($miiCreateId, 0, $charData, 0x18, 8)
    $faceBytes = if ($gender -eq 1) { $defaultFemaleFace } else { $defaultMaleFace }
    [Array]::Copy($faceBytes, 0, $charData, 0x20, 22)

    $rflDir = Join-Path $instanceDirs[$i - 1] 'UserData\NAND\shared2\menu\FaceLib'
    $rflFile = Join-Path $rflDir 'RFL_DB.dat'
    $writeRfl = $true
    if (Test-Path -LiteralPath $rflFile) {
        $existingRfl = [IO.File]::ReadAllBytes($rflFile)
        if ($existingRfl.Length -eq 0x1F1E0) {
            $same = $true
            for ($k = 0; $k -lt 74; $k++) {
                if ($existingRfl[4 + $k] -ne $charData[$k]) { $same = $false; break }
            }
            if ($same) { $writeRfl = $false }
        }
    }
    if ($writeRfl) {
        if (-not (Test-Path -LiteralPath $rflDir)) {
            New-Item -ItemType Directory -Path $rflDir -Force | Out-Null
        }
        $rflDb = New-Object byte[] 0x1F1E0
        # Official DB header 'RNOD' at 0x0000
        $rflDb[0] = 0x52; $rflDb[1] = 0x4E; $rflDb[2] = 0x4F; $rflDb[3] = 0x44
        [Array]::Copy($charData, 0, $rflDb, 4, 74)
        # Hidden/Parade DB header 'RNHD' at 0x1D00
        $rflDb[0x1D00] = 0x52; $rflDb[0x1D01] = 0x4E; $rflDb[0x1D02] = 0x48; $rflDb[0x1D03] = 0x44
        # CRC-16/CCITT at 0x1F1DE..0x1F1DF
        $rflCrc = [MkwRksysCrc]::ComputeRflCrc16($rflDb, 0x1F1DE)
        $rflDb[0x1F1DE] = [byte](($rflCrc -shr 8) -band 0xFF)
        $rflDb[0x1F1DF] = [byte]($rflCrc -band 0xFF)
        [IO.File]::WriteAllBytes($rflFile, $rflDb)
        $miiDbCount++
    }

    # 2. Update License 0 name (0x1C..0x2F) and Mii CreateID (0x30..0x37) in rksys.dat
    $saveFile = Join-Path $instanceDirs[$i - 1] $saveRelPath
    if (-not (Test-Path -LiteralPath $saveFile)) { continue }
    $bytes = [IO.File]::ReadAllBytes($saveFile)
    if ($bytes.Length -lt 0x28000) { continue }
    $matches = $true
    for ($k = 0; $k -lt 20; $k++) {
        if ($bytes[0x1C + $k] -ne $nameBytes[$k]) { $matches = $false; break }
    }
    if ($matches) {
        for ($k = 0; $k -lt 8; $k++) {
            if ($bytes[0x30 + $k] -ne $miiCreateId[$k]) { $matches = $false; break }
        }
    }
    if (-not $matches) {
        [Array]::Copy($nameBytes, 0, $bytes, 0x1C, 20)
        [Array]::Copy($miiCreateId, 0, $bytes, 0x30, 8)
        $crc = [MkwRksysCrc]::Compute($bytes, 0x27FFC)
        $bytes[0x27FFC] = [byte](($crc -shr 24) -band 0xFF)
        $bytes[0x27FFD] = [byte](($crc -shr 16) -band 0xFF)
        $bytes[0x27FFE] = [byte](($crc -shr 8) -band 0xFF)
        $bytes[0x27FFF] = [byte]($crc -band 0xFF)
        [IO.File]::WriteAllBytes($saveFile, $bytes)
        $renamedCount++
    }
}
if ($miiDbCount -gt 0 -or $renamedCount -gt 0) {
    Write-Host "Prepared unique default Miis (12 colors x 2 genders x 3 sizes): $miiDbCount RFL_DB.dat, $renamedCount rksys.dat updated."
}

# --- 4. Bind UDP monitor on 127.0.0.254:27900 before launching ---
$monitorAddr = [Net.IPAddress]::Parse('127.0.0.254')
$monitorPort = 27900
try {
    $udp = New-Object Net.Sockets.UdpClient (New-Object Net.IPEndPoint ($monitorAddr, $monitorPort))
    $udp.Client.ReceiveTimeout = 250
    $udp.Client.ReceiveBufferSize = 2 * 1024 * 1024
} catch {
    throw "Cannot bind $monitorAddr`:$monitorPort - stop any running watch-lab.ps1 or harness first. ($($_.Exception.Message))"
}

function Read-Be32([byte[]]$buf, [int]$off) {
    return ([uint32]$buf[$off] -shl 24) -bor ([uint32]$buf[$off + 1] -shl 16) `
        -bor ([uint32]$buf[$off + 2] -shl 8) -bor [uint32]$buf[$off + 3]
}

function Get-PopCount([uint32]$v) {
    $c = 0
    while ($v -ne 0) {
        $v = $v -band ($v - 1)
        $c++
    }
    return $c
}

# --- 5. Configure shared Aurora pipeline cache, multi-GPU Vulkan distribution & launch instances ---
$prevSharedCache = $env:MKW_SHARED_CACHE_DIR
$prevClockDiv = $env:MKW_TEST_CLOCK_DIVISOR
$prevVkLayerPath = $env:VK_ADD_LAYER_PATH
$prevVkLayers = $env:VK_INSTANCE_LAYERS
$prevVkGpuIndex = $env:MKW_VK_GPU_INDEX
$prevGpuIndex = $env:MKW_GPU_INDEX
$prevHighCount = $env:MKW_HIGH_COUNT
$prevHighCountOpt = $env:MKW_HIGH_COUNT_OPT
$prevKartCullCap = $env:MKW_OPT_KART_CULL
$prevLauncherPriority = $null
try {
    $selfProc = [System.Diagnostics.Process]::GetCurrentProcess()
    $prevLauncherPriority = $selfProc.PriorityClass
    if ($Count -ge 32) { $selfProc.PriorityClass = 'AboveNormal' }
} catch {}
if (-not $SharedCacheDir) {
    $SharedCacheDir = if ($prevSharedCache) { $prevSharedCache } else { Join-Path $LabDir 'SharedCache' }
}
New-Item -ItemType Directory -Path $SharedCacheDir -Force | Out-Null
$env:MKW_SHARED_CACHE_DIR = $SharedCacheDir
if ($ClockDivisor -gt 1) {
    $env:MKW_TEST_CLOCK_DIVISOR = "$ClockDivisor"
} else {
    Remove-Item Env:MKW_TEST_CLOCK_DIVISOR -ErrorAction SilentlyContinue
}

# Detect GPU assignment:
# - Default visible launches use DX12 pinned to the highest-VRAM discrete GPU via MKW_GPU_INDEX
#   (avoids NVIDIA Blackwell >24-instance Vulkan context throttling and uses ~35% less VRAM).
# - Explicit -GraphicsApi vulkan still supports multi-GPU distribution via VK_LAYER_MKW_gpu_select.
$gpuSchedule = @()
$useVkGpuLayer = $false
$effectiveApiGlobal = if ($GraphicsApi) { $GraphicsApi } elseif ($Count -ge 16 -and -not $Headless) { 'vulkan' } elseif (-not $Headless) { 'd3d12' } else { '' }
if (-not $Headless -and $effectiveApiGlobal -eq 'd3d12' -and -not $prevGpuIndex) {
    try {
        if (-not ('MkwDxgiAdapterPicker' -as [type])) {
            Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class MkwDxgiAdapterPicker {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DXGI_ADAPTER_DESC1 {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1 {
        int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
        int GetParent(ref Guid riid, out IntPtr ppParent);
        int EnumAdapters(uint Adapter, out IntPtr ppAdapter);
        int MakeWindowAssociation(IntPtr WindowHandle, uint Flags);
        int GetWindowAssociation(out IntPtr pWindowHandle);
        int CreateSwapChain(IntPtr pDevice, IntPtr pDesc, out IntPtr ppSwapChain);
        int CreateSoftwareAdapter(IntPtr Module, out IntPtr ppAdapter);
        [PreserveSig] int EnumAdapters1(uint Adapter, out IDXGIAdapter1 ppAdapter);
    }
    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter1 {
        int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
        int GetParent(ref Guid riid, out IntPtr ppParent);
        int EnumOutputs(uint Output, out IntPtr ppOutput);
        int GetDesc(out IntPtr pDesc);
        int CheckInterfaceSupport(ref Guid InterfaceName, out long pUMDVersion);
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
    }
    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 ppFactory);
    public static string PickBest() {
        Guid g = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        IDXGIFactory1 fac;
        if (CreateDXGIFactory1(ref g, out fac) != 0 || fac == null) return "";
        int hwIdx = 0, bestIdx = -1;
        ulong bestVram = 0;
        string bestName = "";
        for (uint i = 0; ; i++) {
            IDXGIAdapter1 adp;
            if (fac.EnumAdapters1(i, out adp) != 0) break;
            DXGI_ADAPTER_DESC1 d;
            if (adp.GetDesc1(out d) == 0 && (d.Flags & 2) == 0) {
                ulong vram = (ulong)d.DedicatedVideoMemory;
                if (bestIdx < 0 || vram > bestVram) {
                    bestIdx = hwIdx;
                    bestVram = vram;
                    bestName = d.Description;
                }
                hwIdx++;
            }
            Marshal.ReleaseComObject(adp);
        }
        Marshal.ReleaseComObject(fac);
        if (bestIdx < 0) return "";
        return bestIdx + "|" + bestName + "|" + (bestVram / (1024UL * 1024UL));
    }
}
"@
        }
        $picked = [MkwDxgiAdapterPicker]::PickBest()
        if ($picked) {
            $parts = $picked -split '\|'
            if ($parts.Count -ge 3) {
                $env:MKW_GPU_INDEX = $parts[0]
                Write-Host ("DX12 GPU selected: MKW_GPU_INDEX={0} ({1}, {2} MiB VRAM)" -f $parts[0], $parts[1], $parts[2])
            }
        }
    } catch {}
}
if (-not $Headless -and -not $SingleGpu -and $effectiveApiGlobal -eq 'vulkan' -and
    (Test-Path -LiteralPath (Join-Path $Release 'vk_layer_mkw_gpu_select.dll')) -and
    (Test-Path -LiteralPath (Join-Path $Release 'vk_layer_mkw_gpu_select.json'))) {
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $vkLines = @(& 'vulkaninfo.exe' '--summary' 2>&1 | ForEach-Object { "$_" })
        $vkGpus = @()
        $curIdx = -1
        foreach ($line in $vkLines) {
            if ($line -match '^\s*GPU(\d+):') { $curIdx = [int]$Matches[1] }
            elseif ($curIdx -ge 0 -and $line -match 'deviceName\s*=\s*(.+)$') {
                $vkGpus += [pscustomobject]@{ VkIndex = $curIdx; Name = $Matches[1].Trim(); FreeMiB = 4096 }
                $curIdx = -1
            }
        }
        if ($vkGpus.Count -ge 2) {
            $nvLines = @(& 'nvidia-smi' '--query-gpu=name,memory.free' '--format=csv,noheader,nounits' 2>$null)
            foreach ($nv in $nvLines) {
                $parts = $nv -split ','
                if ($parts.Count -ge 2) {
                    $nvName = $parts[0].Trim()
                    $nvFree = [int]($parts[1].Trim())
                    foreach ($g in $vkGpus) {
                        if ($g.Name -eq $nvName) { $g.FreeMiB = [math]::Max(512, $nvFree) }
                    }
                }
            }
            # Sort largest free VRAM first. Pin all Vulkan instances to the primary
            # high-VRAM GPU (e.g. RTX 5080) up to 48 instances (NVIDIA Windows Vulkan
            # hits a hard ~54 simultaneous VkDevice limit per GPU and crashes in
            # vulkan-1.dll at instance #55+). For >48 instances, overflow onto
            # secondary GPUs (up to 48 per GPU) so 64+ player lobbies launch cleanly.
            $maxPerVkGpu = 48
            $sortedGpus = @($vkGpus | Sort-Object -Property FreeMiB -Descending)
            $gpuCounts = @{}
            for ($k = 0; $k -lt $Count; $k++) {
                $slotGpuIdx = [math]::Min([int][math]::Floor($k / $maxPerVkGpu), $sortedGpus.Count - 1)
                $chosen = $sortedGpus[$slotGpuIdx]
                $gpuSchedule += $chosen.VkIndex
                if (-not $gpuCounts.ContainsKey($chosen.VkIndex)) { $gpuCounts[$chosen.VkIndex] = 0 }
                $gpuCounts[$chosen.VkIndex]++
            }
            $useVkGpuLayer = $true
            $env:VK_ADD_LAYER_PATH = $Release
            $env:VK_INSTANCE_LAYERS = if ($prevVkLayers) { "$prevVkLayers;VK_LAYER_MKW_gpu_select" } else { 'VK_LAYER_MKW_gpu_select' }
            $summaryParts = @()
            foreach ($g in $sortedGpus) {
                if ($gpuCounts.ContainsKey($g.VkIndex)) {
                    $summaryParts += ("GPU{0}={1} ({2} inst, {3} MiB free)" -f $g.VkIndex, $g.Name, $gpuCounts[$g.VkIndex], $g.FreeMiB)
                }
            }
            Write-Host ("Vulkan GPU schedule: {0}" -f ($summaryParts -join '; '))
            if ($Count -gt ($maxPerVkGpu * $sortedGpus.Count)) {
                Write-Warning ("Count={0} exceeds {1} Vulkan instances across {2} GPU(s); NVIDIA Vulkan caps at ~54 VkDevices/GPU. Consider -GraphicsApi d3d12 for 96+ visible instances." -f $Count, ($maxPerVkGpu * $sortedGpus.Count), $sortedGpus.Count)
            }
        }
    } catch {} finally {
        $ErrorActionPreference = $prevEap
        $global:LASTEXITCODE = 0
    }
}

$launched = @()
$success = $false
$startedAt = Get-Date

try {
    if ($GameCubeAdapterUnlimited -and -not $Headless) {
        $gcuProc = @(Get-Process -Name 'GameCubeAdapterUnlimited' -ErrorAction SilentlyContinue)
        if ($gcuProc.Count -eq 0) {
            if ($GcuPath -and (Test-Path -LiteralPath $GcuPath)) {
                Write-Host "Starting GameCubeAdapterUnlimited from $GcuPath ..."
                Start-Process -FilePath $GcuPath -WorkingDirectory (Split-Path -Parent $GcuPath) | Out-Null
                Start-Sleep -Milliseconds 1500
            } else {
                Write-Warning "GameCubeAdapterUnlimited.exe is not running and was not found at '$GcuPath'. Controllers will bind 1:1 once it is started."
            }
        } else {
            Write-Host "GameCubeAdapterUnlimited is already running (1:1 controller mapping enabled: Player N -> Controller N)."
        }
    }

    $aspectTag = if ($useWidescreen) { '16:9' } else { '4:3' }
    $modeLabel = if ($Headless) { 'headless' } else { ('windowed {0}x{1} @ {2}x{3} ({4})' -f $GridCols, $gridRows, $CellWidth, $CellHeight, $aspectTag) }
    if ($AutoDrive) { $modeLabel += ', auto-drive' }
    if ($SharedAudioMix -and -not $Headless) { $modeLabel += ', shared-audio-mix' }
    if ($GameCubeAdapterUnlimited -and -not $Headless) { $modeLabel += ', gc-adapter-unlimited (1:1)' }
    Write-Host ("Starting {0} instance(s) from {1} (capacity {2}, {3}, SharedCache={4})..." -f `
        $Count, $Release, $capacity, $modeLabel, $SharedCacheDir)

    # For >=48 visible instances, stagger client launches by 0.5s so late clients
    # do not sit behind a >60s serial DWC host join queue and hit error page 0x4D.
    $effectiveGap = if ($PSBoundParameters.ContainsKey('Gap')) { $Gap } elseif ($Count -ge 48) { 0.5 } elseif ($Count -ge 32) { 0.05 } else { $Gap }
    if (-not $Headless -and $Count -ge 48) {
        $env:MKW_HIGH_COUNT = '1'
    } else {
        Remove-Item Env:MKW_HIGH_COUNT -ErrorAction SilentlyContinue
    }
    if ($HighCountOpt -and -not $Headless) {
        $env:MKW_HIGH_COUNT_OPT = '1'
    } else {
        Remove-Item Env:MKW_HIGH_COUNT_OPT -ErrorAction SilentlyContinue
    }
    if ($KartCullCap -gt 0 -and -not $Headless) {
        $env:MKW_OPT_KART_CULL = "$KartCullCap"
    } else {
        Remove-Item Env:MKW_OPT_KART_CULL -ErrorAction SilentlyContinue
    }

    $seenGpuWriter = @{}
    for ($i = 1; $i -le $Count; $i++) {
        $instDir = $instanceDirs[$i - 1]
        if ($useVkGpuLayer) {
            $gpuIdx = $gpuSchedule[$i - 1]
            $env:MKW_VK_GPU_INDEX = "$gpuIdx"
            if ($gpuCounts.Count -gt 1) {
                # Keep one Dawn SQLite cache per GPU architecture; let the first instance
                # on each GPU populate it read-write and subsequent ones open read-only.
                $gpuCacheDir = if ($gpuIdx -eq $gpuSchedule[0]) { $SharedCacheDir } else { "${SharedCacheDir}_gpu$gpuIdx" }
                if (-not (Test-Path -LiteralPath $gpuCacheDir)) {
                    New-Item -ItemType Directory -Path $gpuCacheDir -Force | Out-Null
                }
                $env:MKW_SHARED_CACHE_DIR = $gpuCacheDir
                if (-not $seenGpuWriter.ContainsKey($gpuIdx)) {
                    $seenGpuWriter[$gpuIdx] = $true
                    $env:MKW_SHARED_CACHE_READONLY = '0'
                } else {
                    $env:MKW_SHARED_CACHE_READONLY = '1'
                }
            }
        }
        $argsList = @()
        if ($Headless) { $argsList += '--headless' }
        elseif ($SharedAudioMix) { $argsList += '--shared-audio-mix' }
        elseif ($i -gt 1 -and -not $AllAudio) { $argsList += '--no-audio' }
        if ($AutoDrive) { $argsList += '--lan-cpu' }
        if ($GameCubeAdapterUnlimited -and -not $Headless) {
            $argsList += '--gc-adapter-unlimited'
            $argsList += "--controller-slot=$i"
        }
        $argsList += '--lan-local'
        $argsList += "--lan-id=$i"
        $argsList += "--lan-peers=$Peers"
        if ($i -eq 1) { $argsList += '--lan-host' } else { $argsList += '--lan-join' }

        if ($i -gt 1 -and $effectiveGap -gt 0) {
            Start-Sleep -Milliseconds ([int]($effectiveGap * 1000))
        }
        # Redirect stdout/stderr to per-instance files so 48-128 concurrent processes
        # never serialize on the shared Windows Terminal ConPTY pipe.
        $instLogDir = Join-Path $instDir 'UserData\Logs'
        if (-not (Test-Path -LiteralPath $instLogDir)) {
            New-Item -ItemType Directory -Path $instLogDir -Force | Out-Null
        }
        $proc = Start-Process -FilePath (Join-Path $instDir 'WiiCompiledLAN.exe') `
            -ArgumentList $argsList -WorkingDirectory $instDir -NoNewWindow `
            -RedirectStandardOutput (Join-Path $instLogDir 'launch_stdout.log') `
            -RedirectStandardError (Join-Path $instLogDir 'launch_stderr.log') -PassThru
        # Temporarily bump host p01 during lobby join only; all instances are normalized
        # to Normal priority as soon as the lobby is ready so gameplay has 100% parity.
        if ($Count -ge 16 -and $i -eq 1) {
            try { $proc.PriorityClass = 'AboveNormal' } catch {}
        }
        $launched += [pscustomobject]@{ Id = $i; Process = $proc; Directory = $instDir }

        # Wait for Host (instance 1) to open the LAN Friend Room (page 0x9D) before
        # launching clients so clients discover the room on their very first probe.
        if ($i -eq 1 -and $Count -gt 1) {
            $hostWaitStart = Get-Date
            $hostReady = $false
            $hostEp = New-Object Net.IPEndPoint ([Net.IPAddress]::Any, 0)
            while (-not $hostReady -and ((Get-Date) - $hostWaitStart).TotalSeconds -lt 25) {
                if ($proc.HasExited) {
                    throw "Host instance 1 (PID $($proc.Id)) exited unexpectedly with code $($proc.ExitCode); check $instDir\UserData\Logs."
                }
                while ($udp.Available -gt 0) {
                    $pkt = $udp.Receive([ref]$hostEp)
                    if ($pkt.Length -eq 28 -and (Read-Be32 $pkt 0) -eq 0x4D4B574C -and [int](Read-Be32 $pkt 4) -eq 1) {
                        $hPage = [int](Read-Be32 $pkt 20)
                        if ($hPage -eq 0x9D) {
                            $hostReady = $true
                            break
                        }
                    }
                }
                if (-not $hostReady) { Start-Sleep -Milliseconds 100 }
            }
            if ($hostReady) {
                $hs = ((Get-Date) - $hostWaitStart).TotalSeconds
                Write-Host ("Host p01 opened LAN room (page 0x9D) in {0:F1}s; launching {1} client(s)..." -f $hs, ($Count - 1))
            }
        }
    }

    # --- 6. Monitor UDP telemetry until all Count instances are in the lobby ---
    $roomStatus = @{}
    $sdkStatus = @{}
    $inLobbySince = $null
    $lastPrint = Get-Date
    $remoteEp = New-Object Net.IPEndPoint ([Net.IPAddress]::Any, 0)

    while ($true) {
        $elapsed = ((Get-Date) - $startedAt).TotalSeconds
        if ($elapsed -gt $TimeoutSec) {
            throw "Timed out after $TimeoutSec s waiting for all $Count instances to connect into the LAN lobby."
        }

        foreach ($entry in $launched) {
            if ($entry.Process.HasExited) {
                throw "Instance $($entry.Id) (PID $($entry.Process.Id)) exited unexpectedly with code $($entry.Process.ExitCode); check $($entry.Directory)\UserData\Logs."
            }
        }

        # Drain available telemetry packets for up to 250ms.
        while ($udp.Available -gt 0) {
            $pkt = $udp.Receive([ref]$remoteEp)
            if ($pkt.Length -ne 28 -and $pkt.Length -ne 40) { continue }
            $magic = Read-Be32 $pkt 0
            $id = [int](Read-Be32 $pkt 4)
            if ($id -lt 1 -or $id -gt $Count) { continue }
            $now = Get-Date

            if ($magic -eq 0x4D4B574C -and $pkt.Length -eq 28) {
                $state = [int](Read-Be32 $pkt 8)
                $players = [int](Read-Be32 $pkt 12)
                $section = [int](Read-Be32 $pkt 16)
                $page = [int](Read-Be32 $pkt 20)
                $trans = [int](Read-Be32 $pkt 24)
                $roomStatus[$id] = [pscustomobject]@{
                    State = $state; Players = $players; Section = $section
                    Page = $page; Transition = $trans; SeenAt = $now
                }
                if ($page -eq 0x4D -and $trans -eq 4) {
                    throw "Instance $id reached a network error dialog (page 0x4D); inspect $($launched[$id - 1].Directory)\UserData\Logs."
                }
            } elseif ($magic -eq 0x4D4B574E) {
                $role = [int](Read-Be32 $pkt 8)
                $matchState = [int](Read-Be32 $pkt 12)
                $sdkPlayers = [int](Read-Be32 $pkt 16)
                $w0 = Read-Be32 $pkt 20
                $suspended = [int](Read-Be32 $pkt 24)
                $connCount = Get-PopCount $w0
                $maskKey = ('{0:X8}' -f $w0)
                if ($pkt.Length -eq 40) {
                    $w1 = Read-Be32 $pkt 28
                    $w2 = Read-Be32 $pkt 32
                    $w3 = Read-Be32 $pkt 36
                    $connCount += (Get-PopCount $w1) + (Get-PopCount $w2) + (Get-PopCount $w3)
                    $maskKey = ('{0:X8}.{1:X8}.{2:X8}.{3:X8}' -f $w3, $w2, $w1, $w0)
                }
                $sdkStatus[$id] = [pscustomobject]@{
                    Role = $role; MatchState = $matchState; Players = $sdkPlayers
                    ConnectedCount = $connCount; MaskKey = $maskKey
                    Suspended = $suspended; SeenAt = $now
                }
            }
        }

        $now = Get-Date
        $inLobby = 0
        $sdkReady = 0
        $hostMask = if ($sdkStatus.ContainsKey(1)) { $sdkStatus[1].MaskKey } else { '' }
        for ($i = 1; $i -le $Count; $i++) {
            if ($roomStatus.ContainsKey($i)) {
                $r = $roomStatus[$i]
                $expectedState = if ($i -eq 1) { 3 } else { 4 }
                if (($now - $r.SeenAt).TotalSeconds -lt 30 -and
                    $r.State -eq $expectedState -and $r.Players -eq $Count -and
                    $r.Page -eq 0x9D -and $r.Transition -eq 4) {
                    $inLobby++
                }
            }
            if ($sdkStatus.ContainsKey($i)) {
                $s = $sdkStatus[$i]
                $expectedRole = if ($i -eq 1) { 2 } else { 1 }
                $expectedMatch = if ($i -eq 1) { 12 } else { 1 }
                if (($now - $s.SeenAt).TotalSeconds -lt 30 -and
                    $s.Role -eq $expectedRole -and $s.MatchState -eq $expectedMatch -and
                    $s.Players -eq $Count -and $s.ConnectedCount -eq $Count -and
                    $s.Suspended -eq 0 -and $s.MaskKey -eq $hostMask) {
                    $sdkReady++
                }
            }
        }

        if ($inLobby -eq $Count) {
            if ($null -eq $inLobbySince) { $inLobbySince = $now }
        } else {
            $inLobbySince = $null
        }

        $hasSdkTelemetry = ($sdkStatus.Count -gt 0)
        $readyNow = if ($hasSdkTelemetry) {
            ($inLobby -eq $Count -and $sdkReady -eq $Count)
        } else {
            ($null -ne $inLobbySince -and ($now - $inLobbySince).TotalSeconds -ge 5.0)
        }

        if ($readyNow) {
            $success = $true
            $totalSec = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 2)
            $detail = if ($hasSdkTelemetry) { 'page 0x9D, full SDK mesh/Star' } else { 'page 0x9D, held 5s' }
            Write-Host ''
            Write-Host ("READY: All {0}/{0} instances are connected in the LAN lobby ({1}) in {2}s." -f $Count, $detail, $totalSec)
            $launched | Select-Object Id, @{N='PID';E={$_.Process.Id}}, Directory | Format-Table -AutoSize
            break
        }

        if (($now - $lastPrint).TotalSeconds -ge 2.0) {
            $lastPrint = $now
            $hostPlayers = if ($roomStatus.ContainsKey(1)) { $roomStatus[1].Players } else { 0 }
            Write-Host ("[{0,5:F1}s] reporting={1}/{2}  host-room={5}/{2}  in-lobby={3}/{2}  sdk-connected={4}/{2}" -f `
                $elapsed, $roomStatus.Count, $Count, $inLobby, $sdkReady, $hostPlayers)
        }

        Start-Sleep -Milliseconds 100
    }
} finally {
    $udp.Close()
    Remove-Item Env:MKW_SHARED_CACHE_READONLY -ErrorAction SilentlyContinue
    if ($null -eq $prevSharedCache) { Remove-Item Env:MKW_SHARED_CACHE_DIR -ErrorAction SilentlyContinue }
    else { $env:MKW_SHARED_CACHE_DIR = $prevSharedCache }
    if ($null -eq $prevClockDiv) { Remove-Item Env:MKW_TEST_CLOCK_DIVISOR -ErrorAction SilentlyContinue }
    else { $env:MKW_TEST_CLOCK_DIVISOR = $prevClockDiv }
    if ($null -eq $prevVkLayerPath) { Remove-Item Env:VK_ADD_LAYER_PATH -ErrorAction SilentlyContinue }
    else { $env:VK_ADD_LAYER_PATH = $prevVkLayerPath }
    if ($null -eq $prevVkLayers) { Remove-Item Env:VK_INSTANCE_LAYERS -ErrorAction SilentlyContinue }
    else { $env:VK_INSTANCE_LAYERS = $prevVkLayers }
    if ($null -eq $prevVkGpuIndex) { Remove-Item Env:MKW_VK_GPU_INDEX -ErrorAction SilentlyContinue }
    else { $env:MKW_VK_GPU_INDEX = $prevVkGpuIndex }
    if ($null -eq $prevGpuIndex) { Remove-Item Env:MKW_GPU_INDEX -ErrorAction SilentlyContinue }
    else { $env:MKW_GPU_INDEX = $prevGpuIndex }
    if ($null -eq $prevHighCount) { Remove-Item Env:MKW_HIGH_COUNT -ErrorAction SilentlyContinue }
    else { $env:MKW_HIGH_COUNT = $prevHighCount }
    if ($null -eq $prevHighCountOpt) { Remove-Item Env:MKW_HIGH_COUNT_OPT -ErrorAction SilentlyContinue }
    else { $env:MKW_HIGH_COUNT_OPT = $prevHighCountOpt }
    if ($null -eq $prevKartCullCap) { Remove-Item Env:MKW_OPT_KART_CULL -ErrorAction SilentlyContinue }
    else { $env:MKW_OPT_KART_CULL = $prevKartCullCap }
    if ($null -ne $prevLauncherPriority) {
        try { [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = $prevLauncherPriority } catch {}
    }

    if (-not $success -or $StopAfterReady) {
        foreach ($entry in $launched) {
            if ($entry.Process -and -not $entry.Process.HasExited) {
                $entry.Process.Kill()
            }
        }
        if ($StopAfterReady -and $success) {
            Write-Host "Stopped all $Count instances (-StopAfterReady)."
        }
    } else {
        # Normalize all instances to equal Normal priority before gameplay starts.
        foreach ($entry in $launched) {
            if ($entry.Process -and -not $entry.Process.HasExited) {
                try { $entry.Process.PriorityClass = 'Normal' } catch {}
            }
        }
        Write-Host "Lobby is live and ready to play! Stop all instances later with:"
        Write-Host "  .\start-lan-lobby.ps1 -Stop"
    }
}
