# Sea Power Agent Bridge

**把 Sea Power 的战场状态和指挥能力接入 MCP。** 项目提供一个运行在游戏内的桥接插件，以及一个可由 MCP 客户端启动的 Python 转译器。客户端可以读取单位、编组、武器和飞行甲板状态，并通过游戏接口提交航向、航速、航空和交战命令。

[下载 Alpha 0.1.0](https://github.com/BelisariusDora/dsh-seapower-mcp/releases/tag/v0.1.0-alpha) · [桥接文档](doc/README.md) · [MCP 文档](doc/layer2_translation.md)

## 架构

| 层 | 职责 | 进度 |
|---|---|---|
| **1 · 游戏桥接** | C# 插件由 AnchorChain 加载。游戏对象读写在 Unity 主线程执行，通过本机 HTTP 提供原始状态、命令回执和临时触发器。 | 已随 Alpha 提供 |
| **2 · MCP 转译** | Python 服务通过 stdio 接收 MCP 工具调用，转换参数并请求本机 Bridge，保留游戏拒绝、执行结果不确定和部分成功等回执。 | 已随 Alpha 提供，Watch 映射仍需与桥接契约同步 |
| **3 · 工单运行时** | 关联玩家任务、命令和完成条件，消费触发事件，由程序管理结单、超时询问和取消。 | 规划中 |

游戏请求沿 **MCP 客户端 → Python 转译器 → 游戏桥接** 执行。触发事件由桥接独立写入 outbox，供后续工单运行时消费。第一层按订阅采样，直接读取游戏状态。面向玩家的授权和情报策略由上层处理。

## 为什么需要工单

例如玩家要求“让两架飞机返航，确认它们都已回收”。工单运行时将完成条件与当前对局绑定，登记观察后下达返航命令。观察命中时，代码按条件结单并发出提醒。到期仍未完成时，代码询问玩家是否继续等待。等待答复期间，触发器仍可完成工单。

Agent 负责理解意图、生成命令和观察条件，以及接收提醒。采样、计时和状态推进由程序完成，因此等待期间无需反复调用模型。

## 安装 Alpha

1. 按官方指引安装 [AnchorChain Preloader 和 Workshop 模组](https://seapower-modders.github.io/AnchorChain/install-anchor-chain.html)。
2. 下载 Alpha ZIP。关闭游戏后，按包内 `INSTALL.md` 将 `dsh-spmcp` 放入 `Sea Power_Data/StreamingAssets`。
3. 启动游戏，将 MCP 客户端配置为用 Python 通过 stdio 运行部署目录里的 `mcp_server.py`。默认桥接地址为 `127.0.0.1:19080`。

Alpha 是开发与集成预览。直连工具面可读取原始状态并提交游戏命令。当前 Watch 事件可落盘，自动工单结算尚未接通。

## 路线图

1. **对齐 Watch 契约。** 同步 MCP 与 Bridge 的模式、操作符和字符串规则 ID。验证读取失败、读档失效和一次性事件重试。
2. **打通单工单闭环。** 先支持一条命令、一个完成条件和一个询问期限。Agent 离线时，程序仍能消费事件、去重、自动结单并排队提醒。
3. **扩展可控的任务执行。** 加入多条件工单、版本与取消处理，并把玩家授权、正式工具范围和命令记录接到同一运行时。

开发细节见 [第一层桥接](doc/README.md)、[原生触发器](doc/layer1_bridge.md) 和 [第二层转译](doc/layer2_translation.md)。本项目采用 [MIT 许可证](LICENSE)。
