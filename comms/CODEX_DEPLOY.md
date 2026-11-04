# Codex 桌面接入

需要 Windows 上的 Codex 桌面应用、Node.js 24+ 和 PATH 中的 PowerShell 7（pwsh.exe）。

1. 将本目录的 codex.target.example.json 复制为模组目录下的 comms/codex.target.json。填入自己的 Codex 聊天 UUID 与界面显示的准确聊天标题。若聊天标题可能重名，先为目标聊天取一个唯一名称。也可使用 --thread 和 --chat-title 指定目标。
2. 在本 README 所在的 Codex 目录运行转发器：

       node .\codex_link.js

3. 在游戏通信面板将 Target 选为 Codex，发送后切回目标 Codex 聊天。助手会等桌面窗口在前台、能识别目标标题且输入框为空时提交。回复在 Codex 桌面查看。

诊断时可在 Codex 窗口位于前台时运行 node .\codex_link.js --check。该命令只列出可见的聊天标记、编辑框和发送按钮。若界面语言或应用版本使控件名不同，可以传 --composer-name 和 --send-name。

游戏把消息追加到 comms/codex.outbox.jsonl。转发器的同名 .cursor.json 会绑定首次运行所选的线程。遇到提交结果不明确时转发器会停住；检查桌面聊天及游标后再处理，以免重复提交。完整的桌面发送需要在接收者自己的 Codex 版本上验证。
