# AiGameSave v0.1.1

- 增加 Unity 常用 `AppData\\LocalLow` 存档目录检测。
- 读取 Unity `*_Data\\app.info`，自动识别开发商和产品名。
- 内置 ThornSin 规则：`%USERPROFILE%\\AppData\\LocalLow\\ScarletPaper\\ThornSin`。
- 修复备份排除规则误匹配完整路径中的 `Temp` 等目录名。
- 增加真实 ThornSin 检测、备份和恢复集成测试。
