[English](README.md) | **简体中文**

# Mega Man Arena · Maruchi（多人联机增强与跨平台原生无头服务端）

本项目用于在 *Mega Man Arena 5.0* 的基础上实现：
1. **跨平台原生无头服务端（`linux-x64` 与 `win-x64`）**：基于 C# / .NET 10 LTS 实现，可在 **Linux**（无需 Wine / Xvfb）以及 **Windows / Windows Server**（无需显卡、DirectX、音频设备、桌面会话或 GameMaker 运行时）原生运行；
2. **扩大人数上限**：突破原版 4 人房间/对战限制，支持 8 人及以上对战（`Arena8`）；
3. **优化联机体验**：基于 Clean-Room 协议规范重构 Lockstep 转发、提升抗丢包与房间稳定性。

---

## Clean-Room 仓库约定（无版权资产）

为避免侵犯原版游戏版权（DMCA），**本仓库严格不跟踪任何原版游戏二进制、资源文件、修补后的 `.win` 文件、原始反编译 GML 转储或打包压缩包**。

所有受版权保护的原版文件及本地生成产物均存放在 `local/` 目录中，**除 [local/README.md](local/README.md) 与 [local/README.zh-CN.md](local/README.zh-CN.md) 外，`local/` 下的全部文件与子目录均被 `.gitignore` 忽略**（这样 `git clone` 后会自动存在带说明文档的 `local/` 目录，同时保证零版权文件入库）：
- `local/dist/`：可直接发给朋友或上传开服的一键压缩包（`megamanarena-server-win-x64.zip`、`megamanarena-server-linux-x64.tar.gz`、`SHA256SUMS.txt`）以及可选的本地便携客户端整合包（`MegaManArena-Maruchi-Client-Windows.zip`，仅供私人传包、不入库）。
- `local/publish/`：未压缩的独立单文件运行目录，按平台分为 `local/publish/win-x64/` 与 `local/publish/linux-x64/`。
- `local/MegaManArena_v5.0.0.zip`：可选的本地原版 Mega Man Arena 5.0.0 原始发布包（仅在构建 GML 补丁时使用；**仅运行/编译 `Mma.Server` 无需准备**）。
- `local/game/`：自动解压出的原版干净游戏文件（可通过环境变量 `$env:MMA_GAME_DIR` 指定其他路径）。
- `local/tools/`：自动下载的 `UndertaleModCli v0.9.2.0`。
- `local/build/`：GML 补丁编译产出的 `data.server.win`、`data.client8.win`、`data.server8.win`。
- `local/decompiled/`：本地反编译导出的联机相关 GML 参考文本（通过 `.\scripts\Export-OnlineGml.ps1` 生成）。
- `local/runtime/`：本地启动测试时的隔离运行目录（`Music` / `SFX` 通过目录联接复用 `local/game/`）。

---

## 目录结构

```text
megamanarena-maruchi/
├── docs/
│   ├── protocol.md            # Clean-Room 网络协议规范（英文）
│   └── protocol.zh-CN.md      # Clean-Room 网络协议规范（简体中文）
├── src/
│   ├── Mma.Protocol/          # GameMaker UDP 协议常量、包头与二进制读写原语
│   └── Mma.Server/            # 原生跨平台异步 UDP 无头服务端（支持 Linux 与 Windows / Windows Server）
├── tests/
│   └── Mma.Protocol.Tests/    # 协议编解码、配置解析与 UDP 回环单元测试
├── DedicatedServer/           # 原版 4 人专用服 GML 补丁与启动脚本（Patch.csx）
├── Arena8/                    # 8 人 Arena 实验版 GML 补丁与启动脚本（Patch.csx）
├── scripts/                   # 环境初始化、GML 导出、跨平台打包、Windows 与 WSL2 启动脚本
├── local/                     # 仅跟踪说明文档 README*.md；游戏文件、压缩包与构建产物均被忽略
└── MegaManArena.Maruchi.sln
```

---

## 开发与本地测试（Windows + 可选 WSL2）

### 前置条件
- **Windows**：PowerShell（5.1 或 7+）、[.NET 10 SDK](https://dotnet.microsoft.com/)
- **WSL2 (`Ubuntu`)** *(可选，用于本地测试 Linux 服务端)*：建议在 `%USERPROFILE%\.wslconfig` 中开启 `networkingMode=mirrored`，使 Windows 客户端与 WSL2 Linux 服务端直接通过 `127.0.0.1` 互通 UDP。

### 1. 初始化 GML 补丁工具链并构建 `.win` 补丁 *(仅运行 .NET 服务端可跳过)*
将 `MegaManArena_v5.0.0.zip` 放入 `local/` 后，在仓库根目录运行：

```powershell
.\scripts\Setup-DevEnv.ps1
```

该脚本会自动校验原版 `data.win`（SHA-256）、下载 `UndertaleModCli 0.9.2.0` 到 `local/tools/`，并生成 `DedicatedServer` 与 `Arena8` 的补丁产物到 `local/build/`。

如需导出原版联机相关 GML 脚本到本地 `local/decompiled/online/` 供本地协议分析参考：

```powershell
.\scripts\Export-OnlineGml.ps1
```

### 2. 编译与运行 .NET 10 单元测试

```powershell
dotnet test MegaManArena.Maruchi.sln
```

### 3. 打包跨平台独立服务端与一键分发压缩包（`win-x64` 与 `linux-x64`）

生成独立单文件（Self-Contained）运行包及可直接发送的一键压缩包（目标服务器**无需**预装 .NET 运行时、SDK 或原版游戏）：

```powershell
# 默认同时打包 win-x64 与 linux-x64 并生成压缩包（加 -SkipArchive 可跳过打包压缩包）
.\scripts\Publish-Server.ps1

# 或仅打包单个平台：
.\scripts\Publish-WindowsServer.ps1   # -> local/publish/win-x64/ 与 local/dist/megamanarena-server-win-x64.zip
.\scripts\Publish-LinuxServer.ps1     # -> local/publish/linux-x64/ 与 local/dist/megamanarena-server-linux-x64.tar.gz

# 可选：打包包含原版与 Arena8 客户端的便携整合包（含原版资产，仅供私下分发，切勿上传公开仓库）：
.\scripts\Package-WindowsClient.ps1   # -> local/dist/MegaManArena-Maruchi-Client-Windows.zip
```

分发压缩包输出到 `local/dist/`（`megamanarena-server-win-x64.zip`、保留 `0755` 可执行权限的 `megamanarena-server-linux-x64.tar.gz` 及 `SHA256SUMS.txt`）。每个压缩包均包含干净的默认 `server.json`（`local/publish/*/server.json` 仍会保留本机已有修改）、`server.example.json`、快速说明 `README.txt` 及平台启动/防火墙辅助脚本。

### 4. 本地快速启动验证（Windows / WSL2）

```powershell
# 在本机 Windows 直接运行（未打包时自动编译 win-x64，或加 -Build 强制重编）
.\scripts\Run-WindowsServer.ps1 -Build -Port 6520 -MaxPlayers 4 -Mode Arena

# 在 WSL2 Ubuntu 中运行 Linux 版本（未打包时自动编译 linux-x64，或加 -Build 强制重编）
.\scripts\Run-WslServer.ps1 -Build -Port 6520 -MaxPlayers 8 -Mode Arena
```

服务端启动后可使用 UDP 探针 `"MMA_PING"` 验证连通性（预期返回 `"MMA_PONG:<Mode>:<MaxPlayers>"`）。

---

## 生产服务器部署指南

### Windows / Windows Server（`win-x64`）
1. 将 `local/dist/megamanarena-server-win-x64.zip` 发送并解压到目标服务器（含 `Mma.Server.exe`、`server.example.json`、`server.json`、`Start-MmaServer.cmd`、`Open-FirewallPorts.cmd`、`Open-FirewallPorts.ps1`、`README.txt`）。
2. 按需编辑 `server.json`（详见下方[配置项与优先级](#配置项与优先级)）。
3. 放行入站 UDP 双端口（`Port` 与 `Port + 1`）：直接右键 `Open-FirewallPorts.cmd` → **以管理员身份运行**（会自动读取 `server.json` 中的 `Port`，默认放行 `6520` 与 `6521`）；或在管理员 PowerShell 中运行：
   ```powershell
   .\Open-FirewallPorts.ps1 -Port 6520
   ```
   *（若部署端口为 `6528`/`6529` 的 `Arena8` 服务端，请先改 `server.json` 或传入 `-Port 6528`。）*
4. 运行 `Start-MmaServer.cmd` 或 `.\Mma.Server.exe` 启动（可配合任务计划程序或系统服务托管）。服务端完全不依赖显卡、DirectX、声卡、交互式桌面或 GameMaker 运行时，断开 RDP 后亦可稳定运行。

### Linux 服务器（`linux-x64`）
1. 将 `local/dist/megamanarena-server-linux-x64.tar.gz` 上传到 Linux 主机并执行 `tar -xzf megamanarena-server-linux-x64.tar.gz` 解压（已保留 `Mma.Server` 与 `start-server.sh` 的 `0755` 可执行权限；若单独拷贝未解压目录，请先执行 `chmod +x Mma.Server start-server.sh`）。
2. 按需编辑 `server.json`。
3. 在系统防火墙与云安全组中同时放行 **UDP `Port` 与 `Port + 1`**（例如 `sudo ufw allow 6520:6521/udp`）。
4. 运行 `./start-server.sh` 或 `./Mma.Server [参数]`；服务端支持 `SIGINT` / `SIGTERM` 优雅退出，适合 `systemd`、`tmux` 或容器部署。

---

## 防火墙与双 UDP 端口说明

每个服务端实例会同时监听**两个连续的 UDP 端口**：`Port`（控制与 RUDP 会话通道）与 `Port + 1`（局内高速输入转发通道）。两个端口必须在系统防火墙和云安全组中同时放行入站 UDP：

| 规格 | 默认 UDP 端口对 | 适用客户端 |
| :--- | :--- | :--- |
| 原版 4 人模式 | `6520` 与 `6521` | 原版 5.0 客户端（`2..4` 人，支持 `Arena`、`Teams`、`Squads`、`Boss`、`Elevator`、`Stage`） |
| `Arena8`（5–8 人） | `6528` 与 `6529` | 8 人客户端（`Mode=Arena8` 或 `MaxPlayers > 4`） |

---

## 配置项与优先级

配置按**从低到高** 4 个层级覆盖生效：

1. **内置默认值**：`Bind=0.0.0.0`、`Port=6520`、`MaxPlayers=4`、`Mode=Arena`、`Stage=<模式默认关卡>`、`Buffer=3`
2. **`server.json`**：按 `--config` > `MMA_CONFIG` > 当前工作目录 `./server.json` > 程序所在目录 `<exeDir>/server.json` 顺序查找；支持 JSON 注释与尾随逗号；字段名不区分大小写（`Bind`、`Port`、`MaxPlayers` 或别名 `Players`、`Mode`、`Stage`、`Buffer`）。
3. **环境变量** *（非空白值覆盖 `server.json`）*：
   `MMA_CONFIG`、`MMA_BIND`、`MMA_PORT`、`MMA_MAX_PLAYERS`、`MMA_MODE`、`MMA_STAGE`、`MMA_BUFFER`
4. **命令行参数** *（最高优先级，可运行 `Mma.Server --help` 查看）*：
   `--config <路径>`、`--bind <IPv4>`、`--port <0..65534>`、`--max-players <2..64>`、`--mode <Arena|Teams|Squads|Boss|Elevator|Stage|Arena8>`、`--stage <关卡名>`、`--buffer <0..12>`、`--help`（`-h`、`/?`）

---

## 相关文档

- **网络协议规范**：[docs/protocol.md](docs/protocol.md) | [docs/protocol.zh-CN.md](docs/protocol.zh-CN.md)
- **八人同屏客户端补丁（`Arena8`）**：[Arena8/README.md](Arena8/README.md) | [Arena8/README.zh-CN.md](Arena8/README.zh-CN.md)
- **Windows GameMaker 原版 GML 专用服补丁** *（旧版 4 人补丁方案）*：[DedicatedServer/README.md](DedicatedServer/README.md) | [DedicatedServer/README.zh-CN.md](DedicatedServer/README.zh-CN.md)

---

## 贡献者与致谢

- **Lin** — 协作者：贡献了全模式 `Arena8` 八人客户端与抗卡顿联机架构（`240` 号包 Deadline Timeline 防冻结帧提交与 `1.5×` 自动追帧同步、全 6 种模式 8 人解锁、桌面工作区自适应 `SCREEN SIZE: FIT` 缩放、原生贴图半倍八人 HUD 与 2×4 大厅准备网格、多地图 `P5..P8` 出生点与镜头复位锚点生成，以及便携版 Windows 客户端整合包设计）。
