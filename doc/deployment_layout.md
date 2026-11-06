# 模块化部署目录

归属：跨层物理部署布局。参见 [四层文档索引](README.md)。

2026-11-06，部署文件按业务模块归类，源码目录保持原结构；deployment/modules.json 是源码文件到部署位置的唯一清单，由 deploy_modules.ps1 和 build.ps1 复用。

```text
<MOD_ROOT>/
  README.md                     部署说明
  persona.md                    可编辑的通联风格
  setting.ini                   现有用户配置
  mcp_server.py                 原 MCP 路径的兼容入口
  ticket_scheduler.py           原调度命令的兼容入口
  modules/
    runtime_layout.py           路径与入口引导
    mcp/                        MCP、字段投影、轨迹/MLP、Watch 事件读取
    tickets/                    工单调度、持久状态、判据、共享执行契约
    api/                        接口目录、共享 schema、受控业务调用
  harness/
    DSH/                        DSH 收发、插件、执行 Adapter、提示词
      legacy/                   已停用的旧 Node 工单运行时
    Codex/                      每局聊天、app-server 执行 Adapter、提示词
      legacy/                   旧桌面 UI 投递（停用）
  comms/                        队列和运行数据，路径保持原样
  language_cn/、language_en/    游戏语言资源
  portrait/                     头像资源
  shots/                        游戏截图输出
```

Newtonsoft.Json.dll、SeaPowerAgentBridge.dll、构建标记、_info.ini 和 deploy_sha.txt 保持根目录，供既有加载器和构建身份检查使用。AnchorChain.dll 仅作为编译引用，依赖独立安装的 AnchorChain，不随本模组部署或发布。bridge.log 也保留原路径，游戏侧当前写死该位置。目录整理阶段没有重编 C#；后续 Codex 双向收发改动会按正常流程重编 Bridge。Python 自动生成的 __pycache__ 可出现在模块或兼容入口旁，不属于另一份业务实现。

### 兼容入口与启动

原 MCP 客户端配置不必修改：`python "<MOD_ROOT>/mcp_server.py"`。原调度命令 `python "<MOD_ROOT>/ticket_scheduler.py" --out "<MOD_ROOT>/comms" --executor dsh` 同样有效。两个根文件只负责引导，实际代码位于 modules/mcp 与 modules/tickets；直接运行这两个模块目录内的对应入口也已验证。

运行时路径统一从安装布局定位：setting.ini 仍读根目录，Watch 仍读 comms/outbox.jsonl，MLP 权重和 API schema 与各自模块共置。适配器位于相应 Harness 文件夹，普通 Python 导入不需要手动设置 PYTHONPATH。

DSH 插件已兼容 modules/api/api_tools.json，并保留旧平铺目录回退。升级已有安装时须同步插件模块文件到原 DSH 插件包；只移动 JSON 而继续使用旧的插件启动逻辑会失败。当前机器的插件包文件已同步，已经加载的插件仍持有同一份 schema，业务调用通过兼容入口继续运行。

### 部署和旧文件迁移

build.ps1 -Deploy 现在按清单复制模块，再部署两个兼容入口；不会重新把内部 Python 文件平铺到根目录。首次把旧平铺安装整理为模块时，在结束所有活跃工单并停止调度器/收发脚本后，可运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "<SOURCE_ROOT>/deploy_modules.ps1" -ModDir "<MOD_ROOT>" -ArchiveFlatFiles
```

旧平铺代码、旧缓存与 .old 备份默认归档到 `<SEA_POWER_ROOT>/__scratch/deploy_backups/dsh-spmcp-layout-<timestamp>`，在 StreamingAssets 之外，保留不删除。可用 -ArchiveRoot 显式指定备份根目录。源、部署目标与归档目标均检查绝对路径边界；普通后续部署不再次归档。

配置、人设、加载 DLL、语言/头像资源、队列、鉴权、工单日志和图像运行数据不迁移、不覆盖。发布包仍须排除 comms 运行状态、截图、缓存与鉴权文件；不能把旧归档作为新的模组放回 StreamingAssets。

### 已验证范围

候选目录和真实部署均测试了根兼容入口、模块直接入口、83 个工具加载、跨模块导入、setting/Watch/MLP/schema 路径。实际部署的 DSH 注册逻辑成功加载模块化 manifest，经 Python 兼容入口完成接口发现。40 项 Python 回归、DSH 工具和 persona 注入测试通过。调度器已产生新心跳，两个收发脚本已重启，日志无启动异常。

7 个保留文件逐项比对哈希一致：setting.ini、persona.md、Bridge DLL/构建标记、AnchorChain.dll、Newtonsoft.Json.dll、_info.ini。未启动游戏或声称新版 DLL 已在游戏内重新载入；DLL 本身未变化。

可手动复核，不调用游戏或模型：

```powershell
python "<SOURCE_ROOT>/test/deployment_layout_probe.py" "<MOD_ROOT>" --out "<EVIDENCE_DIR>/layout-check.json"
```


异常通知升级新增 modules/tickets/ticket_notifications.py 与 harness/DSH/dsh_ticket_notifications.mjs。DSH 插件包中正在配置的入口和同目录 helper 必须一起更新，并在主会话空闲时重载；health.notificationProtocol=1 确认已加载。详情见 [工单异常通知](ticket_notifications.md)。

Settings 升级新增的 PlayerSettings / SettingsWindow 由原有 C# 构建自动收集，不新增后台进程或 Python 依赖。说明同步到 harness/SETTINGS.md，部署不覆盖已有 setting.ini。见 [Settings 页面](settings_page.md)。
