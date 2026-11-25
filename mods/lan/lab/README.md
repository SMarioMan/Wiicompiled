# WiiCompiled LAN Lab — Launch Scripts & Flags Reference

This folder contains the Windows PowerShell scripts for creating, launching, and monitoring multi-instance Mario Kart Wii LAN sessions (2 to 128 instances).

---

## Quick Start

Run from `Wiicompiled\` in PowerShell:

```powershell
# 24-player visible LAN lobby (recommended single-PC locked 60 FPS build, vanilla visuals)
.\mods\lan\lab\start-lan-lobby.ps1 -Count 24 -Release .\release-lan-24-win64

# 24-player with GameCube Adapter Unlimited (1:1 controllers) + Shared Audio Mix
.\mods\lan\lab\start-lan-lobby.ps1 -Count 24 -Release .\release-lan-24-win64 -SharedAudioMix -GameCubeAdapterUnlimited

# 24-player hands-off CPU auto-drive
.\mods\lan\lab\start-lan-lobby.ps1 -Count 24 -Release .\release-lan-24-win64 -AutoDrive

# 128-player headless test lobby
.\mods\lan\lab\start-lan-lobby.ps1 -Count 128 -Release .\release-lan-128-win64 -Headless -ClockDivisor 4

# Stop all running WiiCompiledLAN instances
.\mods\lan\lab\start-lan-lobby.ps1 -Stop
```

---

## 1. `start-lan-lobby.ps1` (Turnkey All-in-One Launcher)

Automatically prepares NTFS hard-linked instance folders (`lab\p01..pN`), seeds license-0 profiles (`rksys.dat`) with unique `Player 1..N` names, configures a shared Aurora pipeline cache, pins Vulkan instances to the highest-VRAM discrete GPU, launches host + joiners, and waits until all instances are connected in the LAN room (`page 0x9D`).

| Flag | Type / Range | Default | Description |
| --- | --- | --- | --- |
| `-Count` *(pos 0)* | `int` (`2..128`) | `4` | Number of instances to launch on this machine. |
| `-Release` | `string` | Auto-detected | Path to unpacked LAN release folder (e.g., `.\release-lan-24-win64` or `.\release-lan-128-win64`). |
| `-LabDir` | `string` | `<Release>\lab` | Directory for hard-linked instance folders (`p01..pN`). Must be on the same NTFS volume as `-Release`. |
| `-SharedCacheDir` | `string` | `<LabDir>\SharedCache` | Shared Aurora SQLite shader/pipeline cache directory (`MKW_SHARED_CACHE_DIR`). |
| `-Peers` | `int` | Release capacity | Value passed to `--lan-peers=N` (must be `>= -Count` and `<=` build capacity). |
| `-GridCols` | `int` (`0..128`) | `0` (auto) | Number of columns in the tiled window grid (`0` chooses automatically for your display). |
| `-CellWidth` | `int` (`0..8192`) | `0` (auto) | Width of each window in pixels (`0` auto-fits `-ScreenWidth / -GridCols`). |
| `-CellHeight` | `int` (`0..8192`) | `0` (auto) | Height of each window in pixels (`0` auto-fits `-ScreenHeight / rows`). |
| `-ScreenWidth` | `int` (`0..16384`) | `0` (primary monitor) | Target total width for auto-tiling. |
| `-ScreenHeight` | `int` (`0..16384`) | `0` (primary monitor) | Target total height for auto-tiling. |
| `-OriginX` | `int` | `0` | Top-left X screen coordinate for the window grid. |
| `-OriginY` | `int` | `0` | Top-left Y screen coordinate for the window grid. |
| `-Gap` | `double` (seconds) | `0.3` *(auto `0.05` at `>=32`)* | Delay between launching consecutive client processes. |
| `-TimeoutSec` | `int` (`15..3600`) | `300` | Maximum seconds to wait for all instances to connect into the LAN lobby. |
| `-ClockDivisor` | `int` (`1..64`) | `1` | Headless simulation clock divisor (`MKW_CLOCK_DIVISOR`). |
| `-GraphicsApi` | `auto` \| `vulkan` \| `d3d12` | `''` (`vulkan` in config) | Override graphics backend written into instance `Config.toml`. |
| `-ResolutionMultiplier` | `double` (`0.0..4.0`) | `0.0` (auto) | Internal rendering resolution scale multiplier. |
| `-AllAudio` | `switch` | Off | Enable full independent audio output on every instance (default is host `p01` audio only). |
| `-SharedAudioMix` | `switch` | Off | Mix all instances' audio together via shared memory (`--shared-audio-mix`). |
| `-GameCubeAdapterUnlimited` *(alias `-GCUnlimited`)* | `switch` | Off | Launch/attach GameCubeAdapterUnlimited and assign 1:1 controllers (`--gc-adapter-unlimited --controller-slot=N`). |
| `-GcuPath` | `string` | `E:\...\GameCubeAdapterUnlimited.exe` | Path to `GameCubeAdapterUnlimited.exe` when `-GameCubeAdapterUnlimited` is used. |
| `-SingleGpu` | `switch` | Off | Force single-GPU selection without multi-GPU scheduling. |
| `-HighCountOpt` | `switch` | Off | Enable optional runtime visual culling optimizations (`MKW_HIGH_COUNT_OPT=1`) for >12p races (off by default). |
| `-KartCullCap` | `int` (`0..128`) | `0` (default `20` when `-HighCountOpt` is set) | Override `MKW_OPT_KART_CULL` (max nearest frustum-visible karts rendered when `playerCount > 12`). |
| `-AutoDrive` *(alias `-CpuDrive`)* | `switch` | Off | Let CPU AI drive all local racers (`--lan-cpu`). |
| `-Headless` | `switch` | Off | Run all instances without windows or GPU rendering (`--headless`). |
| `-RebuildLab` | `switch` | Off | Force-recreate the `lab\` directory from scratch before launching. |
| `-StopAfterReady` | `switch` | Off | Automatically stop all instances as soon as `Count/Count` reach the LAN lobby (useful for automated tests). |
| `-Stop` | `switch` | Off | Stop all running `WiiCompiledLAN` processes and exit immediately. |

---

## 2. `make-lab.ps1` (Build Instance Folders Only)

Creates the hard-linked `p01..pN` portable instance directories and tiled `UserData\Config.toml` files without launching the game.

| Flag | Type / Range | Default | Description |
| --- | --- | --- | --- |
| `-Release` | `string` | Script directory | Path to unpacked LAN release folder. |
| `-LabDir` | `string` | `<Release>\lab` | Output directory for instance folders (same NTFS volume as `-Release`). |
| `-Instances` | `int` (`2..128`) | `24` | Number of instance folders (`p01..pN`) to create. |
| `-GridCols` | `int` (`1..128`) | `6` | Window grid columns written into `Config.toml`. |
| `-CellWidth` | `int` (`160..8192`) | `640` | Window width per instance. |
| `-CellHeight` | `int` (`90..8192`) | `480` | Window height per instance. |
| `-OriginX` | `int` | `0` | Top-left X offset for the grid. |
| `-OriginY` | `int` | `0` | Top-left Y offset for the grid. |
| `-NandTemplate` | `string` | `''` | Optional existing `UserData\NAND` directory to seed into every instance. |
| `-Peers` | `int` | Release capacity | Peer count validation against build capacity. |
| `-Force` | `switch` | Off | Delete and recreate `-LabDir` if it already exists. |

---

## 3. `run-lab.ps1` (Manual / Multi-PC LAN Launcher)

Launches instances from an existing `lab\` directory. Supports both single-PC loopback (`--lan-local`) and **multi-PC real LAN** sessions (`-Bind`, `-Peer`, `-Ids`, `-HostId`).

```powershell
# Multi-PC 24-player example (Machine A hosts IDs 1..12, Machine B joins IDs 13..24)
# On Machine A (192.168.1.10):
.\mods\lan\lab\run-lab.ps1 -LabDir .\release-lan-24-win64\lab -Instances 12 -Peers 24 -Bind 192.168.1.10 -Peer 192.168.1.20
# On Machine B (192.168.1.20):
.\mods\lan\lab\run-lab.ps1 -LabDir .\release-lan-24-win64\lab -Ids (13..24) -Peers 24 -Bind 192.168.1.20 -Peer 192.168.1.10
```

| Flag | Type / Range | Default | Description |
| --- | --- | --- | --- |
| `-LabDir` | `string` | `<script>\lab` | Path to the `lab\` directory created by `make-lab.ps1`. |
| `-Instances` | `int` (`2..128`) | `24` | Number of instances (`1..Instances`) to launch when `-Ids` is omitted. |
| `-Peers` | `int` | Build capacity | Total room size (`--lan-peers=N`). |
| `-Ids` | `int[]` | `1..Instances` | Explicit list of `--lan-id` values to run on this machine (for splitting across PCs). |
| `-HostId` | `int` (`1..128`) | `1` | Instance ID that receives `--lan-host` (all others receive `--lan-join`). |
| `-Bind` | `string` | `''` (loopback) | Local LAN IP address (`--lan-bind=IP`). Omit for single-PC loopback (`--lan-local`). |
| `-Peer` | `string[]` | `@()` | One or more remote machine LAN IPs (`--lan-peer=IP`) when multicast is filtered. |
| `-SharedCacheDir` | `string` | `<LabDir>\SharedCache` | Shared Aurora pipeline cache directory. |
| `-Headless` | `switch` | Off | Launch instances with `--headless`. |
| `-Gap` | `double` (seconds) | `1.0` | Stagger delay between process launches. |
| `-FirstRun` | `switch` | Off | Create license 0 one instance at a time. |
| `-DryRun` | `switch` | Off | Print generated command lines without launching processes. |
| `-Stop` | `switch` | Off | Stop all running `WiiCompiledLAN` processes. |

---

## 4. `watch-lab.ps1` (Live UDP Telemetry Monitor)

Displays a live table of every loopback LAN instance's room/race state by listening on UDP `127.0.0.254:27900`. Start before launching instances with `run-lab.ps1` (do not run simultaneously with `start-lan-lobby.ps1`, which binds the same monitor port).

| Flag | Type / Range | Default | Description |
| --- | --- | --- | --- |
| `-Address` | `string` | `127.0.0.254` | Local IP address to bind for telemetry packets. |
| `-Port` | `int` (`1..65535`) | `27900` | UDP port to listen on. |
| `-Interval` | `double` (`1..60`) | `1.0` | Table refresh interval in seconds. |

---

## 5. `WiiCompiledLAN.exe` Direct CLI Flags

| Flag | Description |
| --- | --- |
| `--lan-local` | Bind each instance to `127.0.0.<id>` for single-PC multi-instance play. |
| `--lan-id=N` | 1-based instance ID (`1..Peers`). |
| `--lan-peers=N` | Total LAN room capacity (`<=` build capacity). |
| `--lan-host` / `--lan-join` | Auto-navigate menus to host or join the LAN room. |
| `--lan-bind=IP` | Bind to a specific LAN network interface IP. |
| `--lan-peer=IP` | Add a unicast remote LAN peer IP (repeatable). |
| `--lan-cpu` | Let CPU AI drive the local racer in LAN matches. |
| `--headless` | Run without window/GPU rendering. |
| `--no-audio` | Disable audio output/mixing for this instance. |
| `--shared-audio-mix` | Route audio into the shared multi-instance audio mixer. |
| `--gc-adapter-unlimited` | Read GameCube controller state from GameCubeAdapterUnlimited shared memory. |
| `--controller-slot=N` | Pin this instance to 1-based controller slot `N` (`1..128`). |
| `--allow-software-gpu` | Allow software GPU fallback if no hardware adapter is available. |
| `--race[=<courseId>]` | Boot directly into a race/battle (course `0x00..0x29`), bypassing menus. |
| `--race-mode=MODE` | Mode for `--race`: `vs`, `team-vs`, `balloon`, `coin`, `gp`, `tt`. |
| `--race-players=N` | Active player count for `--race` (`1..capacity`). |
| `--race-laps=N` | Lap count for `--race` (`1..9`, race modes only). |
| `--race-cpu` | CPU-driven local racer for `--race`. |
| `--race-cpu-menu-input` | Allow menu input while `--race-cpu` is active. |
| `--race-roster=standard\|diverse` | Vehicle/character roster selection for `--race` (use `standard` for Battle modes). |

---

## 6. Runtime Environment Variables

All regressive / visual-trade-off flags default to **OFF (`0`)** and can be toggled at runtime without rebuilding:

| Env Var | Default | Description |
| --- | --- | --- |
| `MKW_HIGH_COUNT_OPT` | `0` | Master switch for high-player-count (>12p) visual culling optimizations (#1–#5). |
| `MKW_OPT_KART_CULL` | `0` (`20` if master=1) | Nearest `N` frustum-visible karts rendered when `playerCount > 12` (`0` disables). |
| `MKW_OPT_EFFECTS_CULL` | `0` (`1` if master=1) | Skip `Effects::Player` updates & kart drop shadows beyond ~40m or behind camera. |
| `MKW_OPT_NAMEBALLOON_CULL` | `0` (`12` if master=1) | Cap floating player nameplates to nearest `N` visible karts (`0` disables). |
| `MKW_OPT_SKELETAL_CULL` | `0` (`1` if master=1) | Skip `Kart::Sub::UpdateTransforms` & `DriverController::Update` for culled karts. |
| `MKW_OPT_NO_IMGUI_FPS` | `0` (`1` if master=1) | Uniformly hide the per-window ImGui FPS overlay. |
| `MKW_MULTI_INSTANCE` | `0` | Disables single-CCD thread pinning in Aurora so multi-instance workloads scale across all CPU cores (set automatically by `start-lan-lobby.ps1`). |
| `MKW_HIGH_COUNT` | `0` | Caps per-instance Aurora pipeline/texture cache sizes for high instance counts (`>=48`). |
| `MKW_SHARED_CACHE_DIR` | *(none)* | Directory for the shared Aurora SQLite shader cache. |
| `MKW_VK_GPU_INDEX` | *(auto)* | Vulkan physical device index selected by `vk_layer_mkw_gpu_select`. |
| `MKW_PROFILE` | `0` | Print per-second `[MKW_PROFILE]` FPS, upload KB, and submit timing lines to stderr. |

---

## 7. Controls (Without GameCube Adapter Unlimited)

- **USB / Bluetooth Gamepads (Xbox, PlayStation, Switch Pro, etc.):** Plug-and-play via SDL3 with standard GameCube mappings.
- **Keyboard & Remapping:** Keyboard keys are unbound by default; click a game window and press **`F10`** to open the Settings overlay and bind keys under **Controls**.
- **CPU Auto-Drive:** Pass `-AutoDrive` to `start-lan-lobby.ps1`.
