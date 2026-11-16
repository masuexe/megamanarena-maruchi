**English** | [简体中文](README.zh-CN.md)

# Mega Man Arena 5.0 · All-Mode 8-Player + Anti-Stall Build (GML Patch)

Supports **2–8 players** across **all 6 online modes** (`Arena8`, `Teams`, `Squads`, `Boss`, `Elevator`, and `Stage`) with multi-stage `P5..P8` spawns and scrolling-camera anchor recovery. Every player connects using a patched `Arena8` client (`5.0.0.4890-arena8-deadline`), while the host operates without occupying a player slot (via `Mma.Server` or `data.server8.win`).

### Key Features & Collaborator Enhancements (by Lin)
- **Anti-Stall Deadline Timeline (`5..8` players, Packet `240`)**: Server commits frames at 60 Hz on a deadline (`0` neutral mask when a peer drops packets so healthy players never freeze), repairs recovering peers from a 1,024-frame history ring (including `18s` retired-phase retention across Stage -> Boss transitions), and fast-forwards lagging clients at `1.5×` speed. (`2..4` players seamlessly use standard Packet `0` lockstep.)
- **All-Mode 8-Player Compatibility**: Unlocks `Teams` (`color/3` + per-team HUD labels), `Squads`, `Boss` (`objMatchControlBoss` 8P + Wily Machine 8P), `Elevator`, and `Stage` co-op for up to 8 players (`teamNumber = 1` co-op alignment & extra `P5..P8` anchors), plus dynamic mode labels in the lobby.
- **Adaptive Desktop Window Scaling (`SCREEN SIZE: FIT`)**: Automatically scales cleanly at `10:7` within the desktop work area (`Start-Arena8.ps1` `-arena8-work-area=` detection + in-game `Options -> Screen Size: FIT`).
- **Native 8-Player HUD & 2×4 Lobby UI**: Compact dual-column native sprite healthbars, portraits, `P1..P8` labels, tank/stock HUD (`drawStockCount`), and a 2×4 ready grid in the online lobby.
- **Multi-Stage `P5..P8` Spawns**: Dedicated `P5..P8` spawn layouts for Wily Boss / Wily MM3 Stage 2 and automatic collision-checked spawn generation for other Arena maps.

## Build Client & Server Patches

Run `.\scripts\Setup-DevEnv.ps1` from the repository root, or build the 8-player patches standalone (requires stock 5.0 files in `local\game` or `$env:MMA_GAME_DIR`):

```powershell
.\Arena8\Build-Arena8.ps1
```

Outputs are written to the git-ignored `local\build\Arena8\` directory:
- `data.client8.win` (patched from stock `data.win`)
- `data.server8.win` (patched from `local\build\DedicatedServer\data.server.win`)

## Host & Join

1. Verify `local\build\Arena8\data.client8.win` exists (and `data.server8.win` if using the legacy Windows GM host).
2. Host using either the native cross-platform `Mma.Server` (`Mode` in `Arena8|Teams|Squads|Boss|Elevator|Stage`, `Port=6528`, `MaxPlayers=8` on Linux or Windows Server) or `Arena8\Start Arena8 Server.cmd` (`.\Arena8\Start-Arena8.ps1 -Role Server -Mode Arena8`), listening on **UDP 6528 / 6529**.
3. Each player launches `Arena8\Start Arena8 Client.cmd` (or `.\Arena8\Start-Arena8.ps1 -Role Client`), staged in `local\runtime\Arena8-client\`. To create a portable friend pack in `local\dist\MegaManArena-Maruchi-Client-Windows.zip` (strictly local/git-ignored), run `.\scripts\Package-WindowsClient.ps1`.
4. Open **Online -> Direct IP**, enter the host IPv4 (`127.0.0.1` for local testing), and press **Ready** once all connected players (`2..8`) are ready.
5. Run `Arena8\Stop Arena8 Server.cmd` if stopping the legacy Windows GML server.

## Scope & Acknowledgements

Uses dedicated ports (`UDP 6528/6529`) and protocol tag `5.0.0.4890-arena8-deadline` so it stays isolated from stock 4-player lobbies (`UDP 6520/6521`). Thanks to collaborator **Lin** for contributing the all-mode 8-player client support, anti-stall Deadline Timeline protocol (`Packet 240`), `FIT` window scaling, native 8P HUD/lobby layout, and multi-stage `P5..P8` spawns.
