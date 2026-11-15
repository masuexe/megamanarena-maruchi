**English** | [简体中文](README.zh-CN.md)

# Mega Man Arena · Maruchi (Multiplayer Expansion & Native Linux Headless Server)

This project extends *Mega Man Arena 5.0* with three primary goals:
1. **Native Linux Headless Server** built with C# / .NET 10 LTS — runs directly on Linux without Wine, Xvfb, or the Windows GameMaker runtime.
2. **Player Cap Expansion** — breaks the stock 4-player lobby/match limit toward 8+ players.
3. **Multiplayer Netcode Optimization** — improves lockstep resilience, packet-loss handling, and lobby/round stability.

---

## Clean-Room Policy (Zero Copyrighted Assets)

To respect copyright and prevent DMCA issues, **this repository strictly tracks zero original game binaries, audio/sprite assets, patched `.win` data files, raw decompiled GML dumps, or archives**.

All copyrighted or locally generated artifacts live inside the git-ignored `local/` directory:
- `local/MegaManArena_v5.0.0.zip`: User-supplied stock Mega Man Arena 5.0.0 archive (kept strictly local).
- `local/game/`: Automatically extracted stock game files (overridable via `$env:MMA_GAME_DIR`).
- `local/tools/`: Automatically downloaded `UndertaleModCli v0.9.2.0`.
- `local/build/`: Generated GML patch outputs (`data.server.win`, `data.client8.win`, `data.server8.win`).
- `local/decompiled/`: Local decompiled GML reference files generated via `.\scripts\Export-OnlineGml.ps1`.
- `local/runtime/`: Isolated local test runtimes (reusing `Music` / `SFX` via NTFS junctions).
- `local/publish/`: Self-contained Linux `linux-x64` server single-file binary.

---

## Repository Layout

```text
megamanarena-maruchi/
├── src/
│   ├── Mma.Protocol/          # GameMaker UDP protocol constants, headers, and buffer I/O primitives
│   └── Mma.Server/            # Cross-platform async UDP headless server (supports Linux SIGTERM/SIGINT)
├── tests/
│   └── Mma.Protocol.Tests/    # Protocol serialization and UDP loopback tests
├── DedicatedServer/           # Stock 4-player dedicated server GML patch & launcher scripts (Patch.csx)
├── Arena8/                    # 8-player Arena experimental GML patch & launcher scripts (Patch.csx)
├── scripts/                   # Path resolution, dev setup, GML export, Linux publish & WSL2 runner scripts
├── local/                     # (git-ignored) Stock game, UTMT CLI, decompiled refs, .win & publish outputs
└── MegaManArena.Maruchi.sln
```

---

## Quick Start (Windows Development + WSL2 Linux Server Runtime)

### Prerequisites
- **Windows**: PowerShell, [.NET 10 SDK](https://dotnet.microsoft.com/)
- **WSL2 (`Ubuntu`)**: Recommended to set `networkingMode=mirrored` in `%USERPROFILE%\.wslconfig` so Windows game clients can reach the WSL2 Linux server directly via `127.0.0.1` UDP.

### 1. Bootstrap GML Patch Toolchain & Build `.win` Artifacts
Place `MegaManArena_v5.0.0.zip` in `local/`, then run from the repository root:

```powershell
.\scripts\Setup-DevEnv.ps1
```

This verifies stock `data.win` (SHA-256), downloads `UndertaleModCli 0.9.2.0` to `local/tools/`, and compiles the `DedicatedServer` and `Arena8` patches into `local/build/`.

To export online-related GML scripts to `local/decompiled/online/` for local protocol inspection:

```powershell
.\scripts\Export-OnlineGml.ps1
```

### 2. Build & Run .NET 10 Unit Tests

```powershell
dotnet test MegaManArena.Maruchi.sln
```

### 3. Cross-Compile & Run the Linux Headless Server in WSL2

Publish a self-contained single-file `linux-x64` binary (`local/publish/linux-x64/Mma.Server`):

```powershell
.\scripts\Publish-LinuxServer.ps1
```

Build and launch directly inside WSL2 (`Ubuntu`):

```powershell
.\scripts\Run-WslServer.ps1 -Build -Port 6520 -MaxPlayers 8 -Mode Arena
```

You can verify UDP reachability with the `"MMA_PING"` probe (expects `"MMA_PONG:<Mode>:<MaxPlayers>"`).
