**English** | [简体中文](README.zh-CN.md)

# Mega Man Arena 5.0 · Windows Dedicated Server (Stock 4-Player GML Patch)

Creates a dedicated lobby on startup using `server.json`. The host machine requires no gameplay input and does not occupy a player slot. This patch modifies the stock `data.win` via `UndertaleModCli` and [Patch.csx](Patch.csx) and runs on the Windows GameMaker runtime.

> For the **native Linux headless server** that does not require Windows or the GameMaker runtime, see `src/Mma.Server` and the root [README.md](../README.md).

## Build & Setup

From the repository root, run the environment setup script once (unpacks `local/MegaManArena_v5.0.0.zip` to `local/game/`, downloads `UndertaleModCli 0.9.2.0` to `local/tools/`, and outputs patched `.win` files to `local/build/`):

```powershell
.\scripts\Setup-DevEnv.ps1
```

Or rebuild `local\build\DedicatedServer\data.server.win` standalone:

```powershell
.\DedicatedServer\Build-DedicatedServer.ps1
# Optional overrides:
# $env:MMA_GAME_DIR = 'C:\path\to\MegaManArena5.0'
# .\DedicatedServer\Build-DedicatedServer.ps1 -UndertaleModCli 'C:\path\to\UndertaleModCli.exe'
```

The build verifies the SHA-256 of stock Mega Man Arena 5.0 `data.win`.

## Modes & Player Count (`server.json`)

Edit `DedicatedServer\server.json` (defaults to 4-player Arena):

```json
{
  "Mode": "Arena",
  "Players": 4
}
```

`Players` accepts `2`, `3`, or `4`. The match starts once all players connect and press **Ready**.

| `Mode` | Gameplay | Stage |
| --- | --- | --- |
| `Arena` | Free-for-all | Wily Holo Chamber |
| `Teams` | Team battle (distinct color sets) | Wily Holo Chamber |
| `Squads` | Squad elimination | Wily Holo Chamber |
| `Boss` | Co-op boss battle | Mecha Dragon |
| `Elevator` | Co-op elevator survival | Enemy Elevator |
| `Stage` | Co-op stage clear | Bomb Man Stage |

## Launch & Connect

1. Run `DedicatedServer\Start Server.cmd` (or `.\DedicatedServer\Start-DedicatedServer.ps1`). The launcher stages an isolated runtime in `local\runtime\DedicatedServer` and listens on **UDP 6520**.
2. Players launch stock `MegaManArena.exe`, open **Online -> Direct IP**, and connect to the host IPv4 (`127.0.0.1` locally, or LAN/VPN IP with UDP 6520 allowed).
3. Run `DedicatedServer\Stop Server.cmd` to stop the server. Logs are written to `local\runtime\DedicatedServer\server-*.log`.
