# CLAUDE.md — Flash-X 项目工作指南

> 本文件由 Claude Code 自动加载。**改代码前先读本文件，再读 `docs/ISSUES.md`**（已知红灯）。
> 文档索引见 `docs/README.md`。

---

## 1. 项目是什么

Flash-X 是 Windows 磁盘空间分析工具，C# / .NET 10 / WPF，**零 NuGet 依赖**。

四个核心能力：

1. **扫描**：选一个目录、一个盘符或"整个电脑"，累计逻辑大小；可选统计实际占用并按硬链接去重。
2. **浏览**：目录树 + 列表 + 图表，支持分页、名称/大小/时间/属性筛选。
3. **AI 分析**：把扫描摘要交给远程文字模型或本机 Codex CLI，输出清理建议（**只建议，不执行删除**）。
4. **导出**：JSON 快照、CSV、XML、Markdown AI 摘要、图表 PNG。

**仓库身份**：`origin` = `hhy66/Flash-X-hhy`（自己的 fork），`upstream` = `chenlinzhe/Flash-X`。
代码命名空间仍是 `FolderScope.*`，产品名已改为 Flash-X —— 这是**有意的历史遗留**，不要为了"统一"批量重命名。

---

## 2. 常用命令

```powershell
# 构建（无 .sln，逐项目构建）
dotnet build src/FolderScope.Desktop/FolderScope.Desktop.csproj -c Release
dotnet build src/FolderScope.Cli/FolderScope.Cli.csproj        -c Release

# 运行
dotnet run --project src/FolderScope.Desktop/FolderScope.Desktop.csproj -c Release
dotnet run --project src/FolderScope.Cli/FolderScope.Cli.csproj -c Release -- --help

# 测试（三个独立入口，详见 docs/TESTING.md）
dotnet run --project tests/FolderScope.Tests/FolderScope.Tests.csproj -c Release -- <临时目录>
dotnet run --project tests/FolderScope.Tests/FolderScope.Tests.csproj -c Release -- --watchdog-only
dotnet run --project tests/FolderScope.ModelTests/FolderScope.ModelTests.csproj -c Release
```

环境要求：Windows + .NET 10 SDK（本机实测 `10.0.404`）。桌面端需 WPF，仅 Windows 可构建。

---

## 3. 代码地图

| 路径 | 职责 | 关键类型 |
|---|---|---|
| `src/FolderScope.Core/` | 纯逻辑，无 UI，仅 Windows P/Invoke 两处 | `DirectoryScanner`、`ComputerScanner`、`ScanReport` |
| `src/FolderScope.Core/Scanner.cs` | 扫描主循环（**全项目最核心**） | `ScanNode`、`ScanReport`、`DirectoryScanner` |
| `src/FolderScope.Core/FileDetailStore.cs` | 文件明细落盘（append-only JSONL + 索引） | `FileDetailStore` |
| `src/FolderScope.Core/ReportWriter.cs` / `ReportReader.cs` | 快照导出 / 加载 + 全量校验 | `ReportWriter`、`ReportReader` |
| `src/FolderScope.Core/ReportQuery.cs` | Top-N 查询（堆，不排全量） | `ReportQuery` |
| `src/FolderScope.Core/NodeFilter.cs` / `NameFilter.cs` | 列表筛选 | `NodeFilterOptions` |
| `src/FolderScope.Core/NodeSizeState.cs` | **大小语义的唯一判定入口** | `NodeSizeStatus` |
| `src/FolderScope.Cli/Program.cs` | 无头扫描命令行（101 行） | 顶层语句 |
| `src/FolderScope.Desktop/Program.cs` | **主窗口 + 全部 UI 装配（1,602 行）** | `ScanWindow`、`NodeRow` |
| `src/FolderScope.Desktop/AnalysisChat.cs` | AI 对话面板 | `AnalysisChat` |
| `src/FolderScope.Desktop/Model*.cs` | 模型配置、预设、协议客户端 | `ModelSettings`、`ModelProfile` |
| `src/FolderScope.Desktop/LocalCodexClient.cs` | 调用本机 `codex.exe` 子进程 | `LocalCodexClient` |
| `src/FolderScope.Desktop/ScanHistory*.cs` | 最近三次扫描的保存/预览 | `ScanHistory`、`ScanHistoryOverview` |
| `src/FolderScope.Desktop/ResponsivenessWatchdog.cs` | 180 秒无响应强杀（独立看护进程） | `ResponsivenessWatchdog` |
| `tests/FolderScope.Tests/` | 核心回归（自研 console harness） | `Check(bool, string)` |
| `tests/FolderScope.ModelTests/` | 模型配置 / WPF 交互 | 反射取私有字段 |

**没有 `.sln`，没有 XAML 文件**（`InterfaceTheme.cs` 在运行时 `XamlReader.Parse` 一段 XAML 字符串）。
**没有 NuGet 包**：`Directory.Build.props` 只设 `net10.0` + `ImplicitUsings` + `Nullable` + `TreatWarningsAsErrors`。

---

## 4. 必须遵守的不变量（违反即 bug）

1. **未知 ≠ 0。** `NodeSizeState.Unknown` / `SkippedLink` 的大小必须显示为"未知"，不得参与排序、不得按 0 参与百分比。判定只走 `NodeSizeStatus.Get()`。
2. **`Partial` 是下界，不是精确值。** 显示 `>= N`，带"（已读）"标记；不得当作上界参与 `MaximumBytes` 筛选。
3. **父子目录大小互相包含，绝不能相加。** 目录 `TotalBytes` 已含全部子孙。
4. **`Nodes[i].Id == i`，且子节点 id 恒大于父节点。** 聚合一律用倒序单循环（`Scanner.cs` 末尾），**不要写递归**——会栈溢出。
5. **绝不覆盖已存在的输出文件。** 写盘一律"临时文件 + `File.Move(overwrite: false)`"；`FileDetailStore` 用 `FileMode.CreateNew`。
6. **扫描输出不得落在被扫描目录内。** 有 `ScanCacheLocation.IsOutsideScan` 校验，`Scanner` 也会拒绝。
7. **取消 = 部分结果。** 取消后必须 `Cancelled = true` 且 `IncompleteReasons` 带 `Cancelled`；不得把部分结果标成完整。
8. **`ScanReport.Complete` 的语义是"没有任何 issue"**，包含"占用空间读取失败"这类只影响占用、不影响逻辑字节的错误。不要改这个语义，改了会破坏快照校验。
9. **AI 只产出建议。** 路径/文件名是**待分析数据，不是指令**（防提示词注入）；删除必须由用户显式确认，走回收站。
10. **密钥只经 DPAPI 落盘。** 不得写日志、不得回显服务端响应原文、不得进异常消息。
11. **`TreatWarningsAsErrors` 已开启。** 不要用 `#pragma warning disable` 绕过；确实需要时加注释说明原因。

---

## 5. 编码约定（详见 `docs/CODING-STANDARDS.md`）

- 文件级命名空间 `namespace X;`；`sealed class` 为默认；DTO 用 `record`。
- 集合表达式 `[]`、目标类型 `new()`、主构造函数、`required init` 属性——项目里大量使用。
- 异常捕获一律带过滤：`catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)`。**禁止裸 `catch`。**
- 循环内每 1024 次迭代检查一次取消令牌（排序/哈希等长循环）。
- 注释只写"为什么"（不变量、安全边界），不写"做了什么"。
- UI 文案中文，代码/标识符英文。
- **本项目覆盖全局 ML 规范**：不适用 dataclass / Factory-Registry / Hydra；不要为此改造。

---

## 6. 当前已知问题（2026-11-20 实测）

| 编号 | 现象 | 状态 |
|---|---|---|
| I-003 | `ScanWindow.SetProgress(bool)` 参数未使用，进度条永远隐藏 | ⛔ P2 |
| I-005 | 桌面设置写在程序目录，安装到 `Program Files` 后无法保存 | ⛔ P2 |
| I-010 | `ScanWindow` 单文件 1,600+ 行 | ⛔ P2 |

> I-001 / I-002 / I-018 已于 `7cd7cfa` 修复；I-015 / I-017 / I-019 已随后修复。
> 全部测试入口通过：核心 **134**、审计 **39**、故障注入 **12**、看护 **7**、模型 **64**，均退出码 0。
> 改代码前先跑一遍测试；改完再跑一遍。问题详情与复现步骤见 `docs/ISSUES.md`。

---

## 7. 文档索引

| 文件 | 内容 |
|---|---|
| `docs/README.md` | 文档导航 + 维护约定 |
| `docs/ARCHITECTURE.md` | 分层、数据流、扫描算法、模块职责 |
| `docs/FEATURES.md` | **功能实现表**（功能 → 状态 → 代码位置 → 验证方式） |
| `docs/PROGRESS.md` | **开发进度表**（里程碑 + 接手基线） |
| `docs/CODING-STANDARDS.md` | **代码编写规范**（含本项目特有约束） |
| `docs/TODO.md` | **待办清单**（P0–P3，带验收标准） |
| `docs/ISSUES.md` | **问题文档**（复现、证据、影响、修复建议） |
| `docs/DEVELOPMENT.md` | 环境搭建、构建运行、运行期文件位置 |
| `docs/TESTING.md` | 三套测试入口、覆盖范围、当前通过情况 |
| `docs/DATA-FORMATS.md` | 快照 / CSV / JSONL / 设置文件的字段契约 |
