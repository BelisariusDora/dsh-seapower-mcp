# Sea Power Agent Bridge

**把 Sea Power 的战场状态和指挥能力接入 MCP。** 项目提供一个运行在游戏内的桥接插件，以及一个可由 MCP 客户端启动的 Python 转译器。客户端可以读取单位、编组、武器和飞行甲板状态，并通过游戏接口提交航向、航速、航空和交战命令。

[下载 Alpha 0.1.0](https://github.com/BelisariusDora/dsh-seapower-mcp/releases/tag/v0.1.0-alpha) · [桥接文档](doc/README.md) · [MCP 文档](doc/layer2_translation.md)

## 安装并接入 MCP

1. 安装 [AnchorChain Preloader 和 Workshop 模组](https://seapower-modders.github.io/AnchorChain/install-anchor-chain.html)。
2. 关闭游戏，解压 Alpha ZIP，将 `dsh-spmcp` 放入 `Sea Power_Data/StreamingAssets`。详细步骤见包内 `INSTALL.md`。
3. 启动游戏并载入场景。在支持本地 stdio MCP 的 AI 客户端里添加服务，让客户端启动 Python 脚本。

使用 `mcpServers` 格式的客户端可以复制以下配置。把路径换成实际安装位置。如果客户端找不到 `python`，将 `command` 换成 Python 可执行文件的绝对路径。

```json
{
  "mcpServers": {
    "seapower": {
      "command": "python",
      "args": [
        "E:/steam/steamapps/common/Sea Power/Sea Power_Data/StreamingAssets/dsh-spmcp/mcp_server.py"
      ]
    }
  }
}
```

MCP 使用 **stdio**。游戏内 Bridge 默认监听 `127.0.0.1:19080`，由 Python 脚本访问。配置生效后，客户端应能发现 `sp_ping`、`sp_units` 等工具。

## AI 调用指南

1. 通过 MCP 的 `tools/list` 获取当前工具名和参数 schema。
2. 调用 `sp_ping({})` 检查游戏桥接，再用 `sp_session_fleet({})` 或 `sp_units({"side":"mine"})` 获取本局的单位 ID。读档或换场景后重新获取 ID。
3. 用 `sp_unit` 读取目标单位详情。按玩家意图选择对应工具提交命令，并在后续读取中核实游戏状态。命令回执为 `submitted` 表示已提交。若回执包含 `uncertain:true` 或 `resend:"forbidden"`，先读回状态核实，再决定下一步。

可用这句话开始验证接入：**“用 Sea Power MCP 检查连接，然后列出我方水面舰艇和它们的单位 ID。”**

Alpha 的直连工具可以读取原始游戏状态并下达指令。Watch 的 MCP 参数与 Bridge 当前契约存在差异，详见 [第二层文档](doc/layer2_translation.md)。

## 架构

| 层 | 职责 | 进度 |
|---|---|---|
| **1 · 游戏桥接** | C# 插件由 AnchorChain 加载。游戏对象读写在 Unity 主线程执行，通过本机 HTTP 提供原始状态、命令回执和临时触发器。 | 已随 Alpha 提供 |
| **2 · MCP 转译** | Python 服务通过 stdio 接收 MCP 工具调用，转换参数并请求本机 Bridge，保留游戏拒绝、执行结果不确定和部分成功等回执。 | 已随 Alpha 提供，Watch 映射仍需与桥接契约同步 |
| **3 · 交互运行时** | 关联玩家任务、命令和完成条件，消费触发事件，由程序管理结单、超时询问和取消。 | 规划中 |

MCP 客户端通过 Python 转译器访问游戏桥接。桥接产生的 Watch 事件写入本地 outbox，供未来的 UI 交互运行时消费。

## 路线图

- **Roleplay 支持**：让 AI 扮演舰队参谋或指挥角色，结合当前战场状态提供符合角色的简报、建议和指令。
- **UI 交互闭环**：在游戏内提交请求、确认行动、查看执行结果与接收触发提醒。等待和完成判定由运行时代码处理。

开发细节见 [第一层桥接](doc/README.md)、[原生触发器](doc/layer1_bridge.md) 和 [第二层转译](doc/layer2_translation.md)。本项目采用 [MIT 许可证](LICENSE)。
