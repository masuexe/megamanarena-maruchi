**English** | [简体中文](README.zh-CN.md)

# Mega Man Arena · Maruchi (Multiplayer Expansion & Cross-Platform Native Headless Server)

This project extends *Mega Man Arena 5.0* with three primary goals:
1. **Cross-Platform Native Headless Server (`linux-x64` & `win-x64`)** built with C# / .NET 10 LTS — runs natively on **Linux** (without Wine or Xvfb) and **Windows / Windows Server** (without a GPU, DirectX, audio hardware, interactive desktop, or GameMaker runtime).
2. **Player Cap Expansion** — breaks the stock 4-player lobby/match limit toward 8+ players (`Arena8`).
3. **Multiplayer Netcode Optimization** — clean-room reimplementation improving lockstep resilience, packet-loss tolerance, and lobby/round stability.

---

## Clean-Room Policy (Zero Copyrighted Assets)

To respect copyright and prevent DMCA issues, **this repository strictly tracks zero original game binaries, audio/sprite assets, patched `.win` data files, raw decompiled GML dumps, or archives**.

All copyrighted or locally generated artifacts live inside `local/`, where **everything except [local/README.md](local/README.md) and [local/README.zh-CN.md](local/README.zh-CN.md) is git-ignored** (Git still creates `local/` on `git clone` so the directory layout is documented cleanly):
- `local/dist/`: Ready-to-share server archives (`megamanarena-server-win-x64.zip`, `megamanarena-server-linux-x64.tar.gz`, `SHA256SUMS.txt`) and optional local Windows client bundle (`MegaManArena-Maruchi-Client-Windows.zip`, strictly private/git-ignored).
- `local/publish/`: Unpacked self-contained single-file headless server bundles under `local/publish/win-x64/` and `local/publish/linux-x64/`.
- `local/MegaManArena_v5.0.0.zip`: Optional user-supplied stock Mega Man Arena 5.0.0 archive (only needed if rebuilding GML patches; **not** needed to build or run `Mma.Server`).
- `local/game/`: Automatically extracted stock game files (overridable via `$env:MMA_GAME_DIR`).
- `local/tools/`: Automatically downloaded `UndertaleModCli v0.9.2.0`.
- `local/build/`: Generated GML patch outputs (`data.server.win`, `data.client8.win`, `data.server8.win`).
- `local/decompiled/`: Local decompiled GML reference files generated via `.\scripts\Export-OnlineGml.ps1`.
- `local/runtime/`: Isolated local test runtimes (reusing `Music` / `SFX` via NTFS junctions).

---

## Repository Layout

```text
megamanarena-maruchi/
├── docs/
│   ├── protocol.md            # Clean-room wire protocol specification (English)
│   └── protocol.zh-CN.md      # Clean-room wire protocol specification (Simplified Chinese)
├── src/
│   ├── Mma.Protocol/          # GameMaker UDP protocol constants, headers, and buffer I/O primitives
│   └── Mma.Server/            # Cross-platform async UDP headless server (Linux & Windows / Windows Server)
├── tests/
│   └── Mma.Protocol.Tests/    # Protocol serialization, configuration, and UDP loopback tests
├── DedicatedServer/           # Stock 4-player dedicated server GML patch & Windows GM scripts (Patch.csx)
├── Arena8/                    # 8-player Arena experimental GML patch & launcher scripts (Patch.csx)
├── scripts/                   # Dev setup, GML export, cross-platform publish, Windows & WSL2 runner scripts
├── local/                     # Tracked only with README*.md; all assets, archives & builds are git-ignored
└── MegaManArena.Maruchi.sln
```

---

## Quick Start (Development & Local Testing)

### Prerequisites
- **Windows**: PowerShell (5.1 or 7+), [.NET 10 SDK](https://dotnet.microsoft.com/)
- **WSL2 (`Ubuntu`)** *(optional, for local Linux testing)*: Recommended to set `networkingMode=mirrored` in `%USERPROFILE%\.wslconfig` so Windows game clients can reach the WSL2 Linux server directly via `127.0.0.1` UDP.

### 1. Bootstrap GML Patch Toolchain & Build `.win` Artifacts *(Optional for server-only builds)*
Place `MegaManArena_v5.0.0.zip` in `local/`, then run from the repository root:

```powershell
.\scripts\Setup-DevEnv.ps1
```

This verifies stock `data.win` (SHA-256), downloads `UndertaleModCli 0.9.2.0` to `local/tools/`, and compiles the `DedicatedServer` and `Arena8` patches into `local/build/`.

To export online-related GML scripts to `local/decompiled/online/` for local inspection:

```powershell
.\scripts\Export-OnlineGml.ps1
```

### 2. Build & Run .NET 10 Unit Tests

```powershell
dotnet test MegaManArena.Maruchi.sln
```

### 3. Publish Self-Contained Server Bundles & Distribution Archives (`win-x64` & `linux-x64`)

Publish both **Windows / Windows Server (`win-x64`)** and **Linux (`linux-x64`)** self-contained single-file bundles and ready-to-share distribution archives (the target server needs **no** `.NET SDK` or stock game files installed):

```powershell
# Publish & archive both win-x64 and linux-x64 (default; pass -SkipArchive for unpacked output only)
.\scripts\Publish-Server.ps1

# Or target a single platform:
.\scripts\Publish-WindowsServer.ps1   # -> local/publish/win-x64/ + local/dist/megamanarena-server-win-x64.zip
.\scripts\Publish-LinuxServer.ps1     # -> local/publish/linux-x64/ + local/dist/megamanarena-server-linux-x64.tar.gz

# Optional: package a portable local Windows client bundle (Stock + Arena8) for private play:
.\scripts\Package-WindowsClient.ps1   # -> local/dist/MegaManArena-Maruchi-Client-Windows.zip (keep out of public Git!)
```

Ready-to-distribute archives are written to `local/dist/` (`megamanarena-server-win-x64.zip`, `megamanarena-server-linux-x64.tar.gz` preserving `0755` executable mode, and `SHA256SUMS.txt`). Each bundle contains the single-file server binary, clean `server.json` (`local/publish/*/server.json` preserves local edits across rebuilds), `server.example.json`, `README.txt`, and platform launch/firewall helpers.

### 4. Local Smoke Launch (Windows & WSL2)

```powershell
# Run natively on Windows (auto-publishes win-x64 if missing, or pass -Build)
.\scripts\Run-WindowsServer.ps1 -Build -Port 6520 -MaxPlayers 4 -Mode Arena

# Run inside WSL2 Ubuntu (auto-publishes linux-x64 if missing, or pass -Build)
.\scripts\Run-WslServer.ps1 -Build -Port 6520 -MaxPlayers 8 -Mode Arena
```

You can verify UDP reachability with the `"MMA_PING"` probe (expects `"MMA_PONG:<Mode>:<MaxPlayers>"`).

---

## Deploying to Production Servers

### Windows / Windows Server (`win-x64`)
1. Copy or extract `local/dist/megamanarena-server-win-x64.zip` (containing `Mma.Server.exe`, `server.example.json`, `server.json`, `Start-MmaServer.cmd`, `Open-FirewallPorts.cmd`, `Open-FirewallPorts.ps1`, and `README.txt`) to your Windows host.
2. Edit `server.json` if needed (see [Configuration & Precedence](#configuration--precedence)).
3. Open inbound UDP firewall ports (`Port` and `Port + 1`): right-click `Open-FirewallPorts.cmd` -> **Run as administrator** (automatically reads `Port` from `server.json`, defaulting to `6520` & `6521`), or from an Administrator PowerShell prompt run:
   ```powershell
   .\Open-FirewallPorts.ps1 -Port 6520
   ```
   *(Pass `-Port 6528` if hosting an `Arena8` server on port `6528`/`6529`.)*
4. Double-click `Start-MmaServer.cmd` or run `.\Mma.Server.exe` from PowerShell / Task Scheduler / Windows Service wrapper. No GPU, DirectX, audio hardware, interactive RDP session, or GameMaker runtime is required.

### Linux (`linux-x64`)
1. Upload `local/dist/megamanarena-server-linux-x64.tar.gz` and extract with `tar -xzf megamanarena-server-linux-x64.tar.gz` (preserves `0755` executable mode on `Mma.Server` and `start-server.sh`; run `chmod +x Mma.Server start-server.sh` only if transferring unpacked files without mode bits).
2. Edit `server.json` if needed.
3. Allow inbound UDP for **both `Port` and `Port + 1`** in your host firewall and cloud security group (e.g. `sudo ufw allow 6520:6521/udp`).
4. Run `./start-server.sh` (or `./Mma.Server [options]`). The server handles `SIGINT` and `SIGTERM` cleanly for `systemd`, `tmux`, or containers.

---

## Firewall & Dual UDP Ports

Every server instance binds **two consecutive UDP ports**: `Port` (control / RUDP session channel) and `Port + 1` (fast in-match input relay). Both UDP ports must be open inbound:

| Profile | Typical UDP Ports | Client Compatibility |
| :--- | :--- | :--- |
| Stock 4-Player | `6520` & `6521` | Stock 5.0 clients (`2..4` players in `Arena`, `Teams`, `Squads`, `Boss`, `Elevator`, `Stage`) |
| `Arena8` (5–8P) | `6528` & `6529` | Patched `Arena8` clients (`Mode=Arena8` or `MaxPlayers > 4`) |

---

## Configuration & Precedence

Settings resolve in four tiers from **lowest to highest precedence**:

1. **Built-in Defaults**: `Bind=0.0.0.0`, `Port=6520`, `MaxPlayers=4`, `Mode=Arena`, `Stage=<mode default>`, `Buffer=3`
2. **`server.json`**: Discovered via `--config` > `MMA_CONFIG` > `./server.json` (current working directory) > `<exeDir>/server.json`. Supports JSON comments and trailing commas; keys are case-insensitive (`Bind`, `Port`, `MaxPlayers` or `Players` alias, `Mode`, `Stage`, `Buffer`).
3. **Environment Variables** *(non-whitespace overrides `server.json`)*:
   `MMA_CONFIG`, `MMA_BIND`, `MMA_PORT`, `MMA_MAX_PLAYERS`, `MMA_MODE`, `MMA_STAGE`, `MMA_BUFFER`
4. **CLI Flags** *(highest precedence; run `Mma.Server --help`)*:
   `--config <path>`, `--bind <ipv4>`, `--port <0..65534>`, `--max-players <2..64>`, `--mode <Arena|Teams|Squads|Boss|Elevator|Stage|Arena8>`, `--stage <room>`, `--buffer <0..12>`, `--help` (`-h`, `/?`)

---

## Documentation

- **Wire Protocol Specification**: [docs/protocol.md](docs/protocol.md) | [docs/protocol.zh-CN.md](docs/protocol.zh-CN.md)
- **8-Player Client & Legacy Patch (`Arena8`)**: [Arena8/README.md](Arena8/README.md) | [Arena8/README.zh-CN.md](Arena8/README.zh-CN.md)
- **Windows GameMaker GML Server Patch** *(legacy 4P patch)*: [DedicatedServer/README.md](DedicatedServer/README.md) | [DedicatedServer/README.zh-CN.md](DedicatedServer/README.zh-CN.md)

---

## Contributors & Acknowledgements

- **Lin** — Collaborator on the all-mode `Arena8` 8-player client & anti-stall netcode (`Packet 240` Deadline Timeline anti-freeze frame sync & `1.5×` client catch-up, all 6 modes up to 8 players, `SCREEN SIZE: FIT` desktop work-area scaling, native half-scale 8-player sprite HUD & 2×4 lobby grid, multi-stage `P5..P8` spawn placement & camera recovery, and the portable Windows client bundle layout).
