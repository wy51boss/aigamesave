# AiGameSave

面向 Windows 云电脑的单机游戏存档助手 MVP。

## 当前能力

- 输入游戏名称或选择 EXE，扫描经典目录并查询公开资料。
- 批量本地扫描整个游戏目录；只依据通用引擎结构和现有存档文件，不调用 AI、网页搜索或游戏专用规则。
- 通用识别 Unity、Ren'Py、RPG Maker、Unreal、Godot、GameMaker、Wolf RPG 和 NW.js。
- 支持 DeepSeek/OpenAI 兼容 Chat Completions；联网资料先由程序检索，再交给模型分析。
- 两阶段行为检测：开始监听、在游戏内保存、点击“我已保存”。
- 用户确认存档目录后，可以一键备份、一键还原并启动游戏。
- 持久仓库使用 JSON 清单和不可变 ZIP 快照，默认保留最近 10 个快照。
- API Key 使用主密码通过 Argon2id + AES-GCM 加密保存。
- 内置 Elden Ring、Stardew Valley、The Witcher 3、Cyberpunk 2077、Terraria 示例规则。

## 运行

开发环境要求 Windows 10/11 x64 和 .NET 8 SDK：

```powershell
dotnet run --project .\AiGameSave.App\AiGameSave.App.csproj
```

推荐将仓库路径设置到云电脑关机不清空的云硬盘。API Key 和主密码只在用户主动配置时使用；没有 API 时仍可执行规则库、本机扫描和行为检测。

主界面的“批量本地扫描”可选择一个包含多个游戏的目录，并导出带检测依据的 JSON 报告。也可以使用相同的软件服务直接生成报告：

```powershell
dotnet .\AiGameSave.App\bin\Release\net8.0-windows\AiGameSave.App.dll `
  --batch-scan "E:\Games" `
  --report ".\artifacts\software-local-scan.json"
```

报告会明确记录 `usedAi=false`、`usedWebSearch=false` 和 `usedGameSpecificRules=false`。静态扫描不能确认的游戏会显示“需要行为检测”，不会自动填入猜测结果。

## 发布便携版

```powershell
dotnet publish .\AiGameSave.App\AiGameSave.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -o .\artifacts\win-x64
```

当前版本不要求管理员权限，也不使用内核驱动或 ETW。行为检测依赖 `FileSystemWatcher` 和文件元数据变化，最终规则必须由用户确认。
