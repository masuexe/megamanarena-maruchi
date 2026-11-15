[English](README.md) | **简体中文**

# Mega Man Arena · Maruchi（多人联机增强与 Linux 无头服务端）

本项目用于在 *Mega Man Arena 5.0* 的基础上实现：
1. **纯原生 Linux 无头服务端**（基于 C# / .NET 10 LTS，不依赖 Windows 桌面或 GameMaker 运行时）；
2. **扩大人数上限**（突破原版 4 人限制，向 8 人及以上扩展）；
3. **优化联机体验**（解决原版 GML Lockstep 网络抗丢包弱、卡顿全房冻结等问题）。

---

## Clean-Room 仓库约定（无版权资产）

为避免侵犯原版游戏版权（DMCA），**本仓库严格不跟踪任何原版游戏二进制、资源文件、修补后的 `.win` 文件或打包压缩包**。

- 所有受版权保护的原版文件、本地构建产物、运行副本及工具均存放在被 `.gitignore` 忽略的 `local/` 目录中：
  - `local/MegaManArena_v5.0.0.zip`：本地自备的 Mega Man Arena 5.0.0 原始发布包（仅保留在本地，绝不提交）。
  - `local/game/`：自动解压出的原版干净游戏文件（可通过环境变量 `MMA_GAME_DIR` 指定其他路径）。
  - `local/tools/`：自动下载的 `UndertaleModCli v0.9.2.0`。
  - `local/build/`：GML 补丁编译产出的 `data.server.win`、`data.client8.win`、`data.server8.win`。
  - `local/decompiled/`：本地反编译导出的联机相关 GML 参考文本（通过 `.\scripts\Export-OnlineGml.ps1` 生成）。
  - `local/runtime/`：本地启动测试时的隔离运行目录（`Music` / `SFX` 通过目录联接复用 `local/game/`）。
  - `local/publish/`：交叉编译出的 Linux 单文件无头服务端二进制。

---

## 目录结构

```text
megamanarena-maruchi/
├── src/
│   ├── Mma.Protocol/          # GameMaker UDP 协议常量、包头与二进制读写原语
│   └── Mma.Server/            # 原生跨平台异步 UDP 无头服务端（支持 Linux SIGTERM/SIGINT）
├── tests/
│   └── Mma.Protocol.Tests/    # 协议编解码与 UDP 回环单元测试
├── DedicatedServer/           # 原版 4 人专用服 GML 补丁与启动脚本（Patch.csx）
├── Arena8/                    # 8 人 Arena 实验版 GML 补丁与启动脚本（Patch.csx）
├── scripts/                   # 路径解析、环境初始化、GML 导出、Linux 发布与 WSL2 启动脚本
├── local/                     # （已忽略）原版游戏、UTMT 工具、反编译参考、.win 与发布产物
└── MegaManArena.Maruchi.sln
```

---

## 开发环境（Windows 主开发 + WSL2 运行 Linux 服务端）

### 前置条件
- **Windows**：PowerShell、[.NET 10 SDK](https://dotnet.microsoft.com/)
- **WSL2 (`Ubuntu`)**：建议在 `%USERPROFILE%\.wslconfig` 中开启 `networkingMode=mirrored`，使 Windows 客户端与 WSL2 Linux 服务端直接通过 `127.0.0.1` 互通 UDP。

### 1. 初始化 GML 补丁工具链并构建 `.win` 补丁
将 `MegaManArena_v5.0.0.zip` 放入 `local/` 后，在仓库根目录运行：

```powershell
.\scripts\Setup-DevEnv.ps1
```

该脚本会自动校验原版 `data.win`（SHA-256）、下载 `UndertaleModCli 0.9.2.0` 到 `local/tools/`，并生成 `DedicatedServer` 与 `Arena8` 的补丁产物到 `local/build/`。

如需导出原版联机相关 GML 脚本到本地 `local/decompiled/online/` 供协议逆向参考：

```powershell
.\scripts\Export-OnlineGml.ps1
```

### 2. 编译与运行 .NET 10 单元测试

```powershell
dotnet test MegaManArena.Maruchi.sln
```

### 3. 交叉编译并在 WSL2 中运行 Linux 无头服务端

仅打包 `linux-x64` 单文件可执行程序（输出到 `local/publish/linux-x64/Mma.Server`）：

```powershell
.\scripts\Publish-LinuxServer.ps1
```

一键打包并在 WSL2 (`Ubuntu`) 中启动 Linux 无头服务端：

```powershell
.\scripts\Run-WslServer.ps1 -Build -Port 6520 -MaxPlayers 8 -Mode Arena
```

服务端启动后可使用 UDP 探针 `"MMA_PING"` 验证连通性（预期返回 `"MMA_PONG:<Mode>:<MaxPlayers>"`）。
