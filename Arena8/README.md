**English** | [简体中文](README.zh-CN.md)

# Mega Man Arena 5.0 · 8-Player Experimental Build (GML Patch)

Eight players fight in a single free-for-all Wily Holo Chamber match. Every player connects using a patched `Arena8` client, while the host operates without occupying a player slot.

## Build Client & Server Patches

Run `.\scripts\Setup-DevEnv.ps1` from the repository root, or build the 8-player patches standalone (requires stock 5.0 files in `local\game` or `$env:MMA_GAME_DIR`):

```powershell
.\Arena8\Build-Arena8.ps1
```

Outputs are written to the git-ignored `local\build\Arena8\` directory:
- `data.client8.win` (patched from stock `data.win`)
- `data.server8.win` (patched from `local\build\DedicatedServer\data.server.win`)

## Host & Join

1. Verify `local\build\Arena8\data.client8.win` and `data.server8.win` exist.
2. Start the host via `Arena8\Start Arena8 Server.cmd` (or `.\Arena8\Start-Arena8.ps1 -Role Server`). It listens on **UDP 6528** and logs to `local\runtime\Arena8-server\`.
3. Each player launches `Arena8\Start Arena8 Client.cmd` (or `.\Arena8\Start-Arena8.ps1 -Role Client`), staged in `local\runtime\Arena8-client\`.
4. Open **Online -> Direct IP**, enter the host IPv4 (`127.0.0.1` for local testing), and press **Ready** once all 8 players join.
5. Run `Arena8\Stop Arena8 Server.cmd` to shut down the server.

## Scope

Currently limited to 8-player Arena on Wily Holo Chamber over UDP `6528`. Other modes, spectators, replays, and extended multi-host sessions are experimental.
