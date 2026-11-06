# Sea Power Agent Bridge · Alpha 1.0.0

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

游戏内打开 `HARNESS COMMS`。DSH 收发脚本支持 `--game-world`，进入新对局会创建新聊天。新聊天首条游戏消息附带 `harness/DSH/prompts/onceopen.md`，游戏记录显示玩家原文。安装和运行命令见 [DSH](../comms/DSH.md)。[Codex 游戏会话](../comms/CODEX_DEPLOY.md) 使用每局独立的临时 app-server thread，首句新建、同局复用、切局关闭。

项目分层说明见 [源码仓库](https://github.com/BelisariusDora/dsh-seapower-mcp/tree/main/doc)。

### 工单与 Harness 调度

Alpha 1.0 安装包包含 Python 工单调度器、DSH/Codex Adapter 和游戏 Settings。在 `<MOD_ROOT>` 运行 `python .\ticket_scheduler.py --out .\comms`，并安装 DSH 原生插件（协议 2）；聊天收发器保持单独运行。不要同时启动旧 Node 工单运行时。航速任务由调度器观察真值完成；当前也支持通用业务接口任务，executed 表示已记录调用，效果尚未独立验证。配置、迁移与验证边界见源码 `doc/ticket_runtime_plan.md`，部署副本为 `harness/DSH/TICKETS.md`。

共享调度器现在按工单持久化的 executorContext 路由 DSH/Codex Worker，仍只运行一个调度器。`--executor dsh|codex` 指定未显式选择后端时的默认值；游戏 Codex 主会话强制建单到 Codex，不需切换调度器。需要 PATH 中的 codex 与已有登录。STOP 中断主会话，DESTROY 取消工单，约束与恢复机制见 `harness/DSH/TICKETS.md`。

通用接口：构建部署应包括 api_catalog.py、ticket_api.py、api_tools.json；加载新版 DSH 插件并验证 apiTicketProtocol=1，配置后台 Python 路径。首条提示允许主 Agent 建 mcp_task，子代理自带四个快速 API 助手，不必查源码资料。完整配置和边界见 `harness/DSH/TICKETS.md`。运行目录中的 api_calls、图像、鉴权和会话状态不要打包发布。


### 游戏通联 persona

`<MOD_ROOT>/persona.md` 是可编辑的玩家回复风格提示。保留 Harness 的身份、人设和称谓，将游戏回复调整为自然、简短的海军通联；普通受令不说“工单、子代理、回执”等内部术语，不把已下令说成已确认战果。技术调试问题仍可解释实现细节。

DSH 与 Codex 每次转发玩家消息都重新读取此文件并隐藏注入，现有聊天下一句即可采用修改；游戏记录只显示玩家原文。首次安装需重启新版收发脚本，此后改 persona 文件无需重启。缺失或留空时沿用 Harness 默认表达，不阻断通信。构建部署首次复制默认文件，之后保留部署目录中的自定义版本。


### 模块目录

部署按模块收纳：modules/mcp（MCP、投影、轨迹与 Watch）、modules/tickets（工单核心）、modules/api（接口目录与调用），执行 Adapter 放在 harness/DSH、harness/Codex。根目录 mcp_server.py 和 ticket_scheduler.py 是兼容启动入口，原配置和命令继续可用；persona、setting 与加载文件保留原位。旧平铺文件归档在 StreamingAssets 之外。

源码清单为 deployment/modules.json；完整迁移与验证说明见源码 doc/deployment_layout.md，部署副本为 MODULES.md。


### Alpha 1.0 的边界与依赖

AnchorChain Preloader 与 Workshop 模组须独立安装。安装包不含 AnchorChain.dll；源码编译引用已安装的 DLL，构建部署不再把它复制进 dsh-spmcp。升级时可移除本模组目录旧的 AnchorChain.dll，不要删除前置安装位置的组件。

Settings 新增默认关闭的“仅使用主 Agent 操作”：仍建单，DSH/Codex 在原主轮次用受控接口完成业务交互，不派 Worker；保存仅影响新任务。关闭时保留独立 Worker 路径。更多说明见包内 harness/MAIN_AGENT_MODE.md、harness/SETTINGS.md，以及 docs/README.md 的四层架构索引。

已实测两端主模式只读任务、游戏 STOP、经工单协议的销毁，以及关闭模式后的 DSH Worker/异常通知回归。通用 executed 不是战果确认；通用 Watch 自动结单、全量本地化仍未完成。当前是 Alpha 预发布，不把隔离测试或历史实测等同于所有任务和场景均通过。

发布包不携带个人会话、绑定指针、凭证、调用账本或截图，首次启动使用默认配置。源码打包命令：先执行 build.ps1，再运行 python package_release.py --output <OUTPUT_DIR>。打包器从 deployment/assets 和明确的源码清单收集文件，不复制运行中的 comms 目录。
