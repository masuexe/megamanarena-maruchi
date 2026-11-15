[English](README.md) | **简体中文**

# Mega Man Arena 5.0 · Windows 联机服务端（原版 4 人 GML 补丁）

启动时按 `server.json` 创建房间。开服电脑无需操作游戏，也不占玩家位。服务端通过 `UndertaleModCli` 对原版 `data.win` 应用 [Patch.csx](Patch.csx) 补丁，使用原游戏的 Windows GameMaker 运行时运行。

> 若需要不依赖 GPU、DirectX、音频设备或 GameMaker 运行时的**跨平台 .NET 10 原生无头服务端**（`src/Mma.Server`，同时支持原生 **Windows / Windows Server (`win-x64`)** 与 **Linux (`linux-x64`)**，并兼容相同的 `server.json` `Mode` / `Players` 配置格式），请见仓库根目录 [README.zh-CN.md](../README.zh-CN.md)。

## 首次构建与环境准备

在仓库根目录执行一次性环境准备脚本（自动解压 `local/MegaManArena_v5.0.0.zip` 到 `local/game/`，下载 `UndertaleModCli 0.9.2.0` 到 `local/tools/`，并输出补丁文件到 `local/build/`）：

```powershell
.\scripts\Setup-DevEnv.ps1
```

也可以单独重新生成 `local\build\DedicatedServer\data.server.win`：

```powershell
.\DedicatedServer\Build-DedicatedServer.ps1
# 如需指定本地其他版本的原版目录或 CLI 路径：
# $env:MMA_GAME_DIR = 'C:\path\to\MegaManArena5.0'
# .\DedicatedServer\Build-DedicatedServer.ps1 -UndertaleModCli 'C:\path\to\UndertaleModCli.exe'
```

构建脚本会校验原版 `data.win` 的 SHA-256，仅适配 Mega Man Arena 5.0 构建。

## 设置人数和模式

编辑 `DedicatedServer\server.json`。默认配置会创建一个四人自由对战房间：

```json
{
  "Mode": "Arena",
  "Players": 4
}
```

`Players` 可以设为 `2`、`3` 或 `4`，表示开局所需的玩家数。到齐后所有玩家都按 **Ready** 才会开局。

| `Mode` | 玩法 | 地图 |
| --- | --- | --- |
| `Arena` | 自由对战 | Wily Holo Chamber |
| `Teams` | 分队对战，玩家在角色选择里选不同队色 | Wily Holo Chamber |
| `Squads` | 先编满角色小队，再按准备 | Wily Holo Chamber |
| `Boss` | 合作打 Boss | Mecha Dragon |
| `Elevator` | 合作打电梯闯关 | Enemy Elevator |
| `Stage` | 合作通关 | Bomb Man 关卡 |

切换时先关服，修改 `Mode` 或 `Players`，保存后重新开服。玩家通过相同 IP 加入新房间。地图按模式固定。

## 启动和连接

1. 双击 `DedicatedServer\Start Server.cmd`（或运行 `.\DedicatedServer\Start-DedicatedServer.ps1`）。启动脚本会在被 Git 忽略的 `local\runtime\DedicatedServer` 中组装独立运行副本并链接 `Music` / `SFX`。
2. 玩家分别启动原版 `MegaManArena.exe`，进入 **Online → Direct IP**，填开服电脑的 IPv4 地址。同一台电脑测试可填 `127.0.0.1`，其他电脑填局域网或虚拟局域网地址（需放行 **UDP 6520**）。
3. 玩家到齐后各自选好角色并按 **Ready**。
4. 双击 `DedicatedServer\Stop Server.cmd` 关服。日志保存在 `local\runtime\DedicatedServer\server-*.log`。
