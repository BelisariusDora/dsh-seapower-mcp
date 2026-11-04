# Sea Power Agent Bridge · Alpha 0.2.0

## 给部署人员和 AI

- `<SEA_POWER_ROOT>` 是包含 `Sea Power.exe` 的安装目录。Steam 中可通过「管理 → 浏览本地文件」找到。
- `<MOD_ROOT>` 是 `<SEA_POWER_ROOT>/Sea Power_Data/StreamingAssets/dsh-spmcp`。
- `<SOURCE_ROOT>` 是含 `build.ps1` 的源码目录。安装 ZIP 时无需源码。

### 安装发布包

1. 安装 [AnchorChain Preloader 和 AnchorChain Workshop 模组](https://seapower-modders.github.io/AnchorChain/install-anchor-chain.html)。
2. 退出游戏。如已有安装，把旧模组备份到 `StreamingAssets` 之外。解压 ZIP，将其中的 `dsh-spmcp` 文件夹放进 `<SEA_POWER_ROOT>/Sea Power_Data/StreamingAssets`，确认本 README 和 `_info.ini` 位于模组根目录。
3. 启动游戏并进入对局。运行 `Invoke-RestMethod http://127.0.0.1:19080/ping`，确认回包成功并检查 `data.build.staleDll`。
4. 在 MCP 客户端配置本地 stdio 服务。将路径替换为你的绝对路径。

```json
{
  "mcpServers": {
    "seapower": {
      "command": "python",
      "args": ["<MOD_ROOT>/mcp_server.py"]
    }
  }
}
```

用 `sp_ping` 验证 MCP，再读本局态势。游戏桥接与聊天转发可独立验证。更新前可单独保存自己的 `setting.ini` 和聊天绑定，按需迁移。

### 从源码部署

将仓库放在 `<SEA_POWER_ROOT>/tools/spmcp_v2`。如果构建脚本找不到 AnchorChain DLL，在当前 PowerShell 会话把 `ANCHORCHAIN_DLL` 设为已安装的 `AnchorChain.dll` 绝对路径。退出游戏后，从游戏根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\spmcp_v2\build.ps1 -Deploy
```

### 游戏通信

游戏内打开 `HARNESS COMMS`。DSH 收发脚本支持 `--game-world`，进入新对局会创建新聊天。新聊天首条游戏消息附带 `harness/DSH/prompts/onceopen.md`，游戏记录显示玩家原文。安装和运行命令见 [DSH](../comms/DSH.md)。[Codex 桌面转发](../comms/CODEX_DEPLOY.md) 仍为 WIP。

项目分层说明见 [源码仓库](https://github.com/BelisariusDora/dsh-seapower-mcp/tree/main/doc)。
