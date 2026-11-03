# Sea Power Agent Bridge

**作者实测，在 Sea Power 中，Codex + GPT‑6.1 Sol 的反应速度和整体表现远优于 DSH + DeepSeek V4.1 flsh 1017。**

**In the author's Sea Power tests, Codex + GPT‑6.1 Sol responded much faster and performed substantially better overall than DSH + DeepSeek V4.1 flsh 1017.**

**把 Sea Power 的战场状态和指挥能力接入 MCP。** 项目提供一个运行在游戏内的桥接插件，以及一个可由 MCP 客户端启动的 Python 转译器。客户端可以读取单位、编组、武器和飞行甲板状态，并通过游戏接口提交航向、航速、航空和交战命令。

**Connect Sea Power's battlefield state and command controls to MCP.** An in-game bridge plugin and a Python adapter let MCP clients inspect units, formations, weapons, and flight decks, then issue navigation, aviation, and engagement orders.

[下载 / Download Alpha 0.1.0](https://github.com/BelisariusDora/dsh-seapower-mcp/releases/tag/v0.1.0-alpha) · [桥接文档 / Bridge docs](doc/README.md) · [MCP 文档 / MCP docs](doc/layer2_translation.md)

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
2. 关闭游戏，解压 Alpha ZIP，将 `dsh-spmcp` 放入 `Sea Power_Data/StreamingAssets`。详细步骤见包内 `INSTALL.md`。<br>
   Close the game, extract the Alpha ZIP, and place `dsh-spmcp` in `Sea Power_Data/StreamingAssets`. See `INSTALL.md` in the ZIP for details.
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

## 架构 / Architecture

| 层 / Layer | 职责 / Responsibility | 进度 / Status |
|---|---|---|
| **1 · 游戏桥接 / Game bridge** | AnchorChain 加载 C# 插件，在 Unity 主线程访问游戏对象，通过本机 HTTP 提供原始状态、命令回执和临时触发器。<br>AnchorChain loads the C# plugin. It accesses game objects on Unity's main thread and exposes raw state, command receipts, and temporary triggers over local HTTP. | 已随 Alpha 提供<br>Available in Alpha |
| **2 · MCP 转译 / MCP adapter** | Python 服务通过 stdio 接收工具调用，转换参数并请求本机 Bridge，保留拒绝、执行结果不确定和部分成功等回执。<br>The Python service translates MCP tool calls over stdio into local bridge requests and preserves refusals, uncertain outcomes, and partial success. | 已随 Alpha 提供，Watch 映射待同步<br>Available in Alpha, with Watch mappings pending alignment |
| **3 · 交互运行时 / Interaction runtime** | 关联玩家任务、命令和完成条件，消费触发事件，处理结单、超时询问和取消。<br>Links player tasks to commands and completion conditions, consumes trigger events, and handles completion, timeout prompts, and cancellation. | 规划中<br>Planned |

MCP 客户端通过 Python 转译器访问游戏桥接。桥接产生的 Watch 事件写入本地 outbox，供未来的 UI 交互运行时消费。

MCP clients reach the game bridge through the Python adapter. The bridge writes Watch events to a local outbox for the planned UI interaction runtime.

## 路线图 / Roadmap

- **Roleplay 支持 / Roleplay support**<br>
  让 AI 扮演舰队参谋或指挥角色，结合当前战场状态提供符合角色的简报、建议和指令。<br>
  Let the AI act as a fleet staff officer or commander, using the current battle state to provide in-character briefings, recommendations, and orders.
- **UI 交互闭环 / In-game UI workflow**<br>
  在游戏内提交请求、确认行动、查看执行结果与接收触发提醒。等待和完成判定由运行时代码处理。<br>
  Submit requests, confirm actions, view results, and receive trigger alerts in-game. The runtime handles waiting and completion checks.

开发细节见 [第一层桥接](doc/README.md)、[原生触发器](doc/layer1_bridge.md) 和 [第二层转译](doc/layer2_translation.md)。本项目采用 [MIT 许可证](LICENSE)。

For implementation details, see the [game bridge](doc/README.md), [native triggers](doc/layer1_bridge.md), and [MCP adapter](doc/layer2_translation.md). The project is licensed under the [MIT License](LICENSE).
