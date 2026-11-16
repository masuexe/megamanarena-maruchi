[English](README.md) | **简体中文**

# Mega Man Arena 5.0 · 八人同屏实验版（GML 补丁）

支持 **2–8 名玩家** 自由对战，兼容 Wily Holo Chamber、Wily MM3 Stage 2 及通用 Arena 地图的 `P5..P8` 出生地与切屏锚点恢复。玩家端使用打了 `Arena8` 补丁的客户端加入，房主不占用玩家位（推荐使用跨平台 `Mma.Server` 或旧版 `data.server8.win`）。

### 主要特性与客户端优化（含协作者 Lin 贡献）
- **自适应窗口缩放（`SCREEN SIZE: FIT`）**：[Start-Arena8.ps1](Start-Arena8.ps1) 自动读取桌面工作区参数并传入 `-arena8-work-area=`，默认开启 `FIT` 档位（可在游戏 `Options -> Screen Size` 切换），保持 `10:7` 等比最大化且不遮挡任务栏。
- **原生贴图 8 人 HUD 与 2×4 大厅网格**：对战画面采用半倍比例左右分栏原生血条/头像/残机 HUD，联机大厅采用清晰的 2×4 `P1..P8` 准备状态卡片。
- **多地图 `P5..P8` 自动生成出生点**：除 `rmWilyBoss`、`rmWilyMM3Stage2` 专用点位外，其余地图会自动避开实心墙体与已有出生点生成 `P5..P8`，并支持滚动镜头下的脱屏复位锚点。
- **8 人锁步网络调优**：增加 `12ms` 输入发包节流、关闭超过 4 人非回滚模式下的多余单帧锁死等待、预填 `0..onlineBuffer` 初始空输入并修复确认队列清理索引。

## 构建八人版客户端与服务端补丁

在仓库根目录执行环境初始化，或单独运行 `Build-Arena8.ps1`（需要 `local\game` 或 `$env:MMA_GAME_DIR` 中有原版 5.0 游戏文件）：

```powershell
.\Arena8\Build-Arena8.ps1
```

脚本会在被 Git 忽略的 `local\build\Arena8\` 中生成：
- `data.client8.win`（基于原版 `data.win`）
- `data.server8.win`（基于 `local\build\DedicatedServer\data.server.win`）

## 开服和加入

1. 确认已完成构建（`local\build\Arena8\` 中存在 `data.client8.win`）。
2. 服务端推荐使用跨平台 `Mma.Server`（配置 `Mode=Arena8`、`Port=6528`、`MaxPlayers=8`），或双击旧版 `Arena8\Start Arena8 Server.cmd`（`.\Arena8\Start-Arena8.ps1 -Role Server`），监听 **UDP 6528 / 6529**。
3. 玩家端双击 `Arena8\Start Arena8 Client.cmd`（或运行 `.\Arena8\Start-Arena8.ps1 -Role Client`），运行副本位于 `local\runtime\Arena8-client\`。如需打包给朋友直接解压即玩，可在本地运行 `.\scripts\Package-WindowsClient.ps1` 生成便携包到被 Git 忽略的 `local\dist\MegaManArena-Maruchi-Client-Windows.zip`。
4. 在游戏里打开 **Online → Direct IP**，输入服务器 IPv4 地址（同机测试填 `127.0.0.1`），到齐后全员按 **Ready** 即可开局。
5. 若使用旧版 Windows GML 服务端，可双击 `Arena8\Stop Arena8 Server.cmd` 关服。

## 说明与致谢

八人版使用独立的端口（`UDP 6528/6529`）与版本标识 `5.0.0.4890-arena8`，与原版 4 人房（`UDP 6520/6521`）完全隔离。感谢协作者 **Lin** 贡献 `FIT` 自适应窗口、原生八人 HUD 与大厅网格布局、多地图 `P5..P8` 出生点及锁步网络调优。
