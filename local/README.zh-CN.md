[English](README.md) | **简体中文**

# `local/` 本地工作目录（Clean-Room 本地资产与构建产物）

为防止版权 / DMCA 风险并避免向 Git 仓库提交大体积编译产物，**`local/` 目录下的所有本地文件与子目录均默认被 `.gitignore` 忽略**。Git 仓库仅跟踪 `local/README.md` 与 `local/README.zh-CN.md`，用于让仓库克隆（`git clone`）后自动拥有完整的 `local/` 目录与使用说明。

## 按需求选择使用方式

### 1. 仅搭建原生无头服务端（`Mma.Server`）—— 无需原版游戏压缩包
如果你只需要编译、打包或部署跨平台 `.NET 10` 无头服务端（`win-x64` Windows / Windows Server 版与 `linux-x64` Linux 版），**不需要**准备 `MegaManArena_v5.0.0.zip`。

在仓库根目录执行：
```powershell
.\scripts\Publish-Server.ps1
```

将在此目录生成：
- `local/dist/`：可直接分发给朋友或上传服务器的一键压缩包（`megamanarena-server-win-x64.zip`、`megamanarena-server-linux-x64.tar.gz`、`SHA256SUMS.txt`）。
- `local/publish/win-x64/` 与 `local/publish/linux-x64/`：已解压的独立单文件运行目录，方便本机快速调试。

### 2. 需要构建 GML 补丁 / 八人客户端（`Arena8` / `DedicatedServer`）
如果你还需要打包 `Arena8` 八人客户端补丁、旧版原版 4 人专用服 `.win` 补丁或导出原版联机脚本参考：
1. 将自备的 **`MegaManArena_v5.0.0.zip`** 放到 `local/MegaManArena_v5.0.0.zip`（或自行解压并通过 `$env:MMA_GAME_DIR` 指定路径）。
2. 在仓库根目录执行：
   ```powershell
   .\scripts\Setup-DevEnv.ps1
   ```

脚本会自动在本目录生成以下被 Git 忽略的子目录：
- `local/game/`：自动解压出的原版 *Mega Man Arena 5.0.0* 游戏文件（构建前自动校验 `data.win` SHA-256）。
- `local/tools/`：自动下载的 `UndertaleModCli v0.9.2.0`。
- `local/build/`：GML 补丁编译产物（`DedicatedServer/data.server.win`、`Arena8/data.client8.win`、`Arena8/data.server8.win`）。
- `local/decompiled/`：通过 `.\scripts\Export-OnlineGml.ps1` 导出的本地联机 GML 参考文本。
- `local/runtime/`：本地试跑客户端/服务端时的隔离运行副本（通过目录联接复用 `Music/` 与 `SFX/`）。
