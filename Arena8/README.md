# Mega Man Arena 5.0 · 八人同屏实验版（GML 补丁）

八名玩家在同一张 Wily Holo Chamber 地图里自由对战。每名玩家使用打过 `Arena8` 客户端补丁的客户端加入。开服电脑没有玩家位，也不需要操作游戏。

## 构建八人版客户端与服务端补丁

在仓库根目录执行环境初始化，或单独运行 `Build-Arena8.ps1`（需要 `local\game` 或 `$env:MMA_GAME_DIR` 中有原版 5.0 游戏文件）：

```powershell
.\Arena8\Build-Arena8.ps1
```

脚本会在被 Git 忽略的 `local\build\Arena8\` 中生成：
- `data.client8.win`（基于原版 `data.win`）
- `data.server8.win`（基于 `local\build\DedicatedServer\data.server.win`）

## 开服和加入

1. 确认已完成构建（`local\build\Arena8\` 中存在 `data.client8.win` 与 `data.server8.win`）。
2. 双击 `Arena8\Start Arena8 Server.cmd`（或运行 `.\Arena8\Start-Arena8.ps1 -Role Server`），等待命令窗口提示服务端正在运行。八人服使用 **UDP 6528**，运行副本和日志位于 `local\runtime\Arena8-server\`。
3. 玩家端双击 `Arena8\Start Arena8 Client.cmd`（或运行 `.\Arena8\Start-Arena8.ps1 -Role Client`），运行副本位于 `local\runtime\Arena8-client\`。
4. 在游戏里打开 **Online → Direct IP**，输入开服电脑的 IPv4 地址。同一台电脑测试可输入 `127.0.0.1`。八人到齐后各按 **Ready**。
5. 双击 `Arena8\Stop Arena8 Server.cmd` 关闭八人服。

## 实验范围

当前为八人 Arena 单地图版本，使用独立的客户端、服务端和端口。其他模式、观战、录像和跨机长时间联机还未验证。
