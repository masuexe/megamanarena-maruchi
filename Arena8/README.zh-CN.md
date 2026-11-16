[English](README.md) | **简体中文**

# Mega Man Arena 5.0 · 全模式八人同屏 + 抗卡顿联机版（GML 补丁）

支持 **2–8 名玩家** 体验**全 6 种联机模式**（`Arena8` 自由对战、`Teams` 分队、`Squads` 小队、`Boss` 打 Boss、`Elevator` 电梯闯关、`Stage` 合作通关），并支持多地图 `P5..P8` 出生地与切屏锚点恢复。玩家端使用 `Arena8` 补丁客户端（`5.0.0.4890-arena8-deadline`），房主不占用玩家位（推荐使用跨平台 `Mma.Server` 或 `data.server8.win`）。

### 核心特性与客户端优化（协作者 [Lin](https://github.com/xiouaoxi) 贡献）
- **抗卡顿截止时间线同步（`5..8` 人对局，`240` 号包）**：服务端按 `60 Hz` 截止时间提交全员帧状态；个别玩家短暂丢包时暂按松开按键（`0`）提交而不冻结全房；对端恢复后由 `1024` 帧环形历史与 `18s` 旧阶段保留窗自动补发修复批次，客户端队列超 `buffer + 8` 帧时自动 `1.5×` 倍速追帧。（`2–4` 人局继续无缝兼容标准 `0` 号包锁步。）
- **全模式 8 人解锁**：适配 `Teams`（色系分组与队伍标签着色）、`Squads`、`Boss`（`objMatchControlBoss` 与威利机器 8 人支持）、`Elevator` 与 `Stage` 合作同队及额外玩家锚点，大厅标题实时显示当前模式名。
- **自适应窗口缩放（`SCREEN SIZE: FIT`）**：[Start-Arena8.ps1](Start-Arena8.ps1) 自动读取桌面工作区参数传入 `-arena8-work-area=`，默认开启 `FIT` 档位（可在游戏 `Options -> Screen Size` 切换），保持 `10:7` 等比最大化且不遮挡任务栏。
- **原生贴图 8 人 HUD 与 2×4 大厅网格**：半倍比例左右分栏原生血条/头像/残机/能量罐 HUD（含合作模式 `drawStockCount` 缩放）及 2×4 `P1..P8` 大厅卡片。
- **多地图 `P5..P8` 自动生成出生点**：支持 `rmWilyBoss`、`rmWilyMM3Stage2` 专用点位与通用 Arena 避墙防重叠生成。

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
2. 服务端推荐使用跨平台 `Mma.Server`（配置 `Mode` 为 `Arena8`/`Teams`/`Squads`/`Boss`/`Elevator`/`Stage`、`Port=6528`、`MaxPlayers=8`），或通过 `.\Arena8\Start-Arena8.ps1 -Role Server -Mode Arena8` 启动旧版 Windows GML 服，监听 **UDP 6528 / 6529**。
3. 玩家端双击 `Arena8\Start Arena8 Client.cmd`（或运行 `.\Arena8\Start-Arena8.ps1 -Role Client`），运行副本位于 `local\runtime\Arena8-client\`。如需打包给朋友直接解压即玩，可在本地运行 `.\scripts\Package-WindowsClient.ps1` 生成便携包到被 Git 忽略的 `local\dist\MegaManArena-Maruchi-Client-Windows.zip`。
4. 在游戏里打开 **Online → Direct IP**，输入服务器 IPv4 地址（同机测试填 `127.0.0.1`），人数 ≥ 2 且当前全员按 **Ready** 即可开局。
5. 若使用旧版 Windows GML 服务端，可双击 `Arena8\Stop Arena8 Server.cmd` 关服。

## 说明与致谢

八人版使用独立的端口（`UDP 6528/6529`）与版本标识 `5.0.0.4890-arena8-deadline`，与原版 4 人房（`UDP 6520/6521`）完全隔离。感谢协作者 [**Lin** (`@xiouaoxi`)](https://github.com/xiouaoxi) 贡献全模式八人解锁、`Packet 240` 抗卡顿截止时间线方案、`FIT` 自适应窗口、原生八人 HUD 与大厅网格及多地图 `P5..P8` 出生点。
