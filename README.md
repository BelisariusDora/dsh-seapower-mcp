# Sea Power Agent Bridge

**作者实测，在 Sea Power 中，Codex + GPT‑6.1 Sol 的反应速度和整体表现远优于 DSH + DeepSeek V4.1 flsh 1017。**

**In the author's Sea Power tests, Codex + GPT‑6.1 Sol responded much faster and performed substantially better overall than DSH + DeepSeek V4.1 flsh 1017.**

**把 Sea Power 的战场状态和指挥能力接入 MCP。** 项目提供一个运行在游戏内的桥接插件，以及一个可由 MCP 客户端启动的 Python 转译器。客户端可以读取单位、编组、武器和飞行甲板状态，并通过游戏接口提交航向、航速、航空和交战命令。

**Connect Sea Power's battlefield state and command controls to MCP.** An in-game bridge plugin and a Python adapter let MCP clients inspect units, formations, weapons, and flight decks, then issue navigation, aviation, and engagement orders.

[下载 / Download Alpha 1.1](https://github.com/BelisariusDora/dsh-seapower-mcp/releases/tag/v1.1.0-alpha) · [完整部署指南 / Deployment guide](doc/DEPLOY.md) · [四层文档 / Architecture docs](doc/README.md) · [MCP 文档 / MCP docs](doc/layer2_translation.md)

## 当前 AI 能力 / What AI can do today

接入 MCP 后，AI 可以按玩家指示读取战场、下达命令和进行图上作业。 / With MCP connected, an AI assistant can inspect the battlefield, issue orders, and work on the tactical map at the player's direction.

| 作业 / Area | | |
|---|---|---|
| **态势感知 / Situational awareness** | 查询己方编组、单位和接触航迹，读取航路、弹药、武器射程、飞行甲板和潜艇状态。 | Inspect friendly task forces, units, and contact tracks. Read routes, magazines, weapon ranges, flight decks, and submarine status. |
| **舰队指挥 / Fleet command** | 对单舰或编组下达航速、航向和航路命令，管理雷达、声呐和电磁管制，校验交战条件并下达攻击或停火指令。 | Set speed, course, and waypoints for individual ships or groups. Manage radar, sonar, and EMCON. Validate engagements and issue attack or ceasefire orders. |
| **舰载机与航空作业 / Carrier and air operations** | 查看甲板、在空飞机和航空任务。指定挂载起飞，指挥返航和回收，取消航空任务或安排重新挂载。 | Inspect flight decks, airborne aircraft, and deck tasks. Launch aircraft with a specified loadout, order return and recovery, abort tasks, or rearm aircraft. |
| **反潜作业 / Anti-submarine warfare** | 查询潜艇状态与深度，控制支持的潜航模式，操作声呐，并指挥具备能力的航空平台投放声呐浮标。 | Inspect submarine status and depth, command supported dive modes, operate sonar, and deploy sonobuoys from capable aircraft. |
| **图上作业 / Tactical map work** | 聚焦和缩放地图，查看或规划航路，测量距离与方位，绘制标记、量程圈、多边形和文字，并获取截图。 | Focus and zoom the map, inspect or plot routes, measure range and bearing, draw markers, range rings, polygons, and labels, and capture screenshots. |

具体命令取决于平台能力和当前场景。提交命令后，可用读取工具核实游戏状态。 / Available commands depend on the platform and scenario. Use read tools to verify the resulting game state after issuing an order.

## 安装并接入 MCP / Install and connect MCP

1. 安装 [AnchorChain Preloader 和 Workshop 模组](https://seapower-modders.github.io/AnchorChain/install-anchor-chain.html)。<br>
   Install the [AnchorChain Preloader and Workshop mod](https://seapower-modders.github.io/AnchorChain/install-anchor-chain.html).
2. 关闭游戏，解压 Alpha ZIP，将 `dsh-spmcp` 放入 `Sea Power_Data/StreamingAssets`。详细步骤见包内 `README.md`。<br>
   Close the game, extract the Alpha ZIP, and place `dsh-spmcp` in `Sea Power_Data/StreamingAssets`. See `README.md` in the ZIP for details.
3. 启动游戏并载入场景。在支持本地 stdio MCP 的 AI 客户端里添加服务，让客户端启动 Python 脚本。<br>
   Start the game and load a scenario. Add the server to an AI client that supports local MCP over stdio so the client can launch the Python script.

使用 `mcpServers` 格式的客户端可以复制以下配置。在 `steamapps` 前补上游戏所在的 Steam 库路径，使脚本路径成为绝对路径。如果客户端找不到 `python`，将 `command` 换成 Python 可执行文件的绝对路径。

If your client uses the `mcpServers` format, copy the configuration below and prefix `steamapps` with the path to the Steam library that contains the game. Use the resulting absolute script path. If the client cannot find `python`, set `command` to the absolute path of the Python executable.

```json
{
  "mcpServers": {
    "seapower": {
      "command": "python",
      "args": [
        "steamapps/common/Sea Power/Sea Power_Data/StreamingAssets/dsh-spmcp/mcp_server.py"
      ]
    }
  }
}
```

MCP 使用 **stdio**。游戏内 Bridge 默认监听 `127.0.0.1:19080`，由 Python 脚本访问。配置生效后，客户端应能发现 `sp_ping`、`sp_units` 等工具。

MCP uses **stdio**. The Python adapter connects to the in-game bridge at `127.0.0.1:19080` by default. Once configured, the client should discover tools such as `sp_ping` and `sp_units`.

## AI 调用指南 / AI tool usage

1. 通过 MCP 的 `tools/list` 获取当前工具名和参数 schema。<br>
   Use MCP `tools/list` to discover the available tools and their parameter schemas.
2. 调用 `sp_ping({})` 检查游戏桥接，再用 `sp_session_fleet({})` 或 `sp_units({"side":"mine"})` 获取本局的单位 ID。读档或换场景后重新获取 ID。<br>
   Call `sp_ping({})` to check the bridge, then call `sp_session_fleet({})` or `sp_units({"side":"mine"})` to obtain unit IDs for the current session. Refresh IDs after loading a save or changing scenarios.
3. 用 `sp_unit` 读取目标单位详情。按玩家意图选择对应工具提交命令，并在后续读取中核实游戏状态。命令回执为 `submitted` 表示已提交。若回执包含 `uncertain:true` 或 `resend:"forbidden"`，先读回状态核实，再决定下一步。<br>
   Use `sp_unit` to inspect the intended unit. Choose the tool that matches the player's request, submit the order, and read back the game state. A `submitted` receipt confirms submission. If a reply contains `uncertain:true` or `resend:"forbidden"`, verify the state before taking further action.

试用提示词 / Example prompt

> 用 Sea Power MCP 检查连接，然后列出我方水面舰艇和它们的单位 ID。
>
> Use Sea Power MCP to check the connection, then list our surface ships and their unit IDs.

Alpha 的直连工具可以读取原始游戏状态并下达指令。Watch 的 MCP 参数与 Bridge 当前契约存在差异，详见 [第二层文档](doc/layer2_translation.md)。

The Alpha tools can read raw game state and issue orders directly. Some MCP Watch parameters differ from the current bridge contract. See the [MCP adapter notes](doc/layer2_translation.md).

## Alpha 1.1 更新 / Changes

- **工单本地化 / Ticket localization**：固定卡片、状态、按钮和详情文本读取 `language_cn/ui.ini`、`language_en/ui.ini`，跟随界面语言；升级只补缺键。任务标题与 Agent 原始回报保留原文。<br>Ticket labels, states, buttons, and details use external language files and follow the UI language. Upgrades preserve custom entries. Agent-generated titles and reports retain their original language.
- **回复语言 / Reply language**：首轮隐藏提示要求按玩家输入语言回复，新会话首句起生效。<br>The first-turn prompt asks the assistant to reply in the player's input language, starting with a new session.
- **部署说明 / Deployment instructions**：包内根 README 覆盖仅 MCP、DSH、Codex 和双后端部署，包含依赖、启动、只读验收和排错；修复 PowerShell 5.1 部署文档的 UTF-8 乱码。<br>The package README covers MCP-only, DSH, Codex, and dual-backend setup, dependencies, startup, read-only checks, and troubleshooting. PowerShell 5.1 deployment now preserves UTF-8 documentation.

本版仍为 **Alpha**。为提供 GitHub **Latest** 下载入口，Release 未使用平台的 Pre-release 标志，这不代表稳定版验收完成。AnchorChain 独立安装；发行包不携带其 DLL、凭据、会话或运行日志。<br>This remains an **Alpha** release. The GitHub Pre-release flag is unset so it can be marked **Latest**; this does not imply stable-release acceptance. Install AnchorChain separately. Its DLL, credentials, sessions, and runtime logs are excluded from the package.

## 架构 / Architecture

| 层 / Layer | 职责 / Responsibility | 文档 / Docs |
|---|---|---|
| **1 · 接口层 / Game bridge** | AnchorChain 加载 C# 插件，在 Unity 主线程读取游戏真值、提交命令，提供 HOLD 和原生 Watch。<br>The C# plugin reads game state and submits commands on Unity's main thread, with HOLD and native Watch support. | [第一层 / Layer 1](doc/layer1_bridge.md) |
| **2 · 转译层 / MCP adapter** | Python 提供 stdio MCP、HTTP 参数和回执转译、字段投影与 MLP。<br>Python exposes stdio MCP, translates HTTP parameters and receipts, and provides field projection and MLP. | [第二层 / Layer 2](doc/layer2_translation.md) |
| **3 · Harness 调度层 / Harness scheduling** | DSH/Codex 本局主会话、工单生命周期、Worker 或仅主 Agent 执行、调用账本和异常回报。<br>Manages per-game DSH/Codex main sessions, tickets, Worker or main-agent-only execution, call evidence, and problem reports. | [第三层 / Layer 3](doc/layer3_workflow.md) |
| **4 · 人机交互层 / Player interaction** | 游戏内通联、工单展开详情、STOP/销毁、Settings、菜单键位、本地化及 persona。<br>Provides in-game chat, ticket details, STOP/destroy controls, Settings, menu key binding, localization, and persona presentation. | [第四层 / Layer 4](doc/layer4_interaction.md) |

直接 MCP 接入只需前两层；游戏内交互需要另行配置 Harness 与工单调度器，不能只启动 MCP 后等待其自动运行。DSH 使用原生插件接入，Codex 使用独立 CLI 会话；两端均有本局独立主会话和 Worker 执行路径。Settings 的“仅主 Agent”默认关闭，开启后仍生成工单，但由主 Agent 负责交互。<br>Direct MCP use requires the first two layers. In-game interaction also requires the Harness and ticket scheduler described in the deployment guide. DSH uses its native plugin, while Codex uses independent CLI sessions. Both support per-game main sessions and Worker execution. Main-agent-only mode is off by default; enabling it retains tickets while assigning interactions to the main Agent.

## 当前边界与后续工作 / Limits and next steps

- `executed` 只表示接口调用获得执行回执，不代表战果确认。通用 Watch 事件读取与证据展示已有，通用 Watch 自动结单尚未接通。<br>`executed` means API calls received execution receipts; it does not confirm combat effects. Watch event reading and evidence display exist, but generic Watch-based automatic completion is not connected yet.
- 工单固定文本已接入语言文件，部分其他通联静态文本仍需完善；动态回报语言由 Harness 和提示词控制。<br>Ticket labels use language files; some other static chat text still needs localization. Harness configuration and prompts govern generated replies.
- 部署步骤及验收范围见 [部署指南](doc/DEPLOY.md)、[当前工单契约](doc/tickets_deployment.md)、[本地化说明](doc/localization.md)。历史 TR 文档保留施工过程，不能替代当前部署步骤。<br>See the [deployment guide](doc/DEPLOY.md), [current ticket contract](doc/tickets_deployment.md), and [localization notes](doc/localization.md). Historical TR documents describe development milestones rather than current startup instructions.

项目采用 [MIT 许可证 / MIT License](LICENSE)。
