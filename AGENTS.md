# 仓库规则

本仓库作为 MCodexCore 的嵌套项目管理。

修改本仓库前，先读取父级 MCodexCore 规则：

- `../AGENTS.md`
- `../.codex/skills/`

如果无法找到父级 MCodexCore 规则，停止操作，并请用户将本仓库按 MCodexCore 嵌套形式放置，或确认正确的父级路径。

## 项目身份

本仓库是 `MTimer`，一个 .NET 8 WPF 桌面计时器项目，作为 MCodexCore 的嵌套子项目管理。

识别本项目时，应结合：

- 本文件
- `README.md`
- `MWPFProject_Timer.sln`
- `MWPFProject_Timer/MWPFProject_Timer.csproj`
- WPF 入口文件 `MWPFProject_Timer/App.xaml`、`MWPFProject_Timer/MainWindow.xaml`
- 项目 default Skill：`../.codex/skills/projects/mtimer/mp-mtimer-default/SKILL.md`

处理本项目时，优先读取并使用上述项目 default Skill。

## 编码与读取

若本仓库包含中文规则、文档或笔记，在 PowerShell 中读取 `AGENTS.md`、`README.md`、Markdown 或 Skill 文件时，应显式使用 UTF-8，例如：

`[IO.File]::ReadAllText($path, [Text.UTF8Encoding]::UTF8)`

若终端显示中文乱码，先用显式 UTF-8 重新读取确认，不要直接判断文件内容损坏。

## 工作边界

修改、迁移、重命名、批量格式化、生成产物或调用外部系统前，必须先说明影响范围、目标文件和验证方式，并等待用户明确确认。

本仓库是独立 Git 仓库，应分别检查父级 MCodexCore 和当前子项目的 Git 状态。
