# 开发环境与运行

## 1. 环境要求

| 项 | 要求 | 本机实测（2026-11-20） |
|---|---|---|
| 操作系统 | Windows（WPF + Win32 占用统计） | Windows 11 Pro for Workstations 10.0.26200 |
| .NET SDK | 10.0.x | `10.0.404` |
| Desktop Runtime | .NET 10 Desktop Runtime | 随 SDK 安装（`10.0.15`，见 `research/install-notes.md` 记录） |
| IDE | 可选（VS / Rider / VS Code + C# Dev Kit） | — |
| 外部依赖 | **无 NuGet 包**；AI 可选本机 `codex.exe` | — |

安装 SDK（管理员 PowerShell）：

```powershell
winget install Microsoft.DotNet.SDK.10
```

> `.NET 8` 无法构建本项目：`Directory.Build.props` 固定 `net10.0`，桌面端 `net10.0-windows`。

---

## 2. 目录结构

```
Flash-X-hhy/
├─ CLAUDE.md                  # AI 助手工作指南（根目录唯一）
├─ README.md                  # 面向使用者
├─ docs/                      # 全部项目文档 + 截图
├─ src/
│  ├─ Directory.Build.props   # 统一 net10.0 + Nullable + TreatWarningsAsErrors
│  ├─ FolderScope.Core/       # 21 个 .cs，纯逻辑
│  ├─ FolderScope.Cli/        # 1 个 .cs，命令行
│  └─ FolderScope.Desktop/    # 21 个 .cs，WPF
└─ tests/
   ├─ FolderScope.Tests/      # 核心回归（2 个 .cs）
   └─ FolderScope.ModelTests/ # 模型/WPF（1 个 .cs）
```

**没有 `.sln`**（见 `ISSUES.md` I-007）。`bin/`、`obj/` 被忽略。

---

## 3. 构建

```powershell
dotnet build src/FolderScope.Core/FolderScope.Core.csproj       -c Release
dotnet build src/FolderScope.Cli/FolderScope.Cli.csproj        -c Release
dotnet build src/FolderScope.Desktop/FolderScope.Desktop.csproj -c Release
dotnet build tests/FolderScope.Tests/FolderScope.Tests.csproj   -c Release
dotnet build tests/FolderScope.ModelTests/FolderScope.ModelTests.csproj -c Release
```

`TreatWarningsAsErrors` 已开启：**有警告即失败**。常见触发点：未使用的变量、可空性、`async` 无 `await`。

产物：

| 项目 | 路径 |
|---|---|
| 桌面 | `src/FolderScope.Desktop/bin/Release/net10.0-windows/FolderScope.Desktop.exe` |
| CLI | `src/FolderScope.Cli/bin/Release/net10.0/FolderScope.Cli.exe` |
| 核心测试 | `tests/FolderScope.Tests/bin/Release/net10.0/FolderScope.Tests.exe` |
| 模型测试 | `tests/FolderScope.ModelTests/bin/Release/net10.0-windows/FolderScope.ModelTests.exe` |

---

## 4. 运行

### 4.1 桌面端

```powershell
dotnet run --project src/FolderScope.Desktop/FolderScope.Desktop.csproj -c Release

# 启动即扫描指定目录
... -- "D:\some\folder"

# 启动即加载快照
... -- --snapshot "D:\reports\scan.json"
```

> 桌面端启动时会拉起一个 `--watch-responsiveness` 看护子进程。若通过 `dotnet` 间接启动导致文件名不是 `FolderScope.Desktop.exe`，会在启动阶段直接弹框退出（见 I-009）。**开发时请直接运行 `bin\Release\...\FolderScope.Desktop.exe`**。

### 4.2 CLI

```powershell
dotnet run --project src/FolderScope.Cli/FolderScope.Cli.csproj -c Release -- --help

# 基本扫描
... -- scan "D:\data"

# 只留目录（省内存）+ 统计占用 + 导出
... -- scan "D:\data" --directories-only --allocated-space --export "D:\out\scan.json"

# 快速估算：只关注 > 100 MiB 的文件
... -- scan "D:\data" --min-file-mib 100

# 文件明细落盘（可浏览单文件，不占内存）
... -- scan "D:\data" --file-details "D:\cache\details.jsonl"
```

**退出码**：

| 码 | 含义 |
|---|---|
| 0 | 扫描完整 |
| 2 | 部分结果 / 有错误 / 跳过链接 |
| 64 | 参数错误 |
| 74 | IO / 权限错误 |
| 130 | 用户取消（Ctrl+C） |

**参数约束**：

- `--export` 可重复；扩展名只接受 `.json` / `.csv` / `.xml`；**目标已存在直接失败**（扫描前校验）。
- `--file-details` 与 `--directories-only` 都会关闭文件节点保留。
- `--min-file-mib` 需要保留文件节点或已给 `--file-details`，否则退出 64。
- `--min-file-mib` / `--min-dir-mib` 只影响**列出的匹配项**，导出的报告始终完整。

### 4.3 测试

见 [`TESTING.md`](TESTING.md)。快速版：

```powershell
dotnet run --project tests/FolderScope.ModelTests/FolderScope.ModelTests.csproj -c Release
dotnet run --project tests/FolderScope.Tests/FolderScope.Tests.csproj -c Release -- --watchdog-only
dotnet run --project tests/FolderScope.Tests/FolderScope.Tests.csproj -c Release -- "$env:TEMP\fs-core"
```

---

## 5. 运行期文件（重要）

> 排查问题、清理、备份时先看这里。**用户数据全部在 `%LOCALAPPDATA%\FolderScope\`**，只有一个例外（见下表 `⚠️`）。

| 路径 | 内容 | 写入方 | 生命周期 |
|---|---|---|---|
| `%LOCALAPPDATA%\FolderScope\model-settings.json` | AI 模型配置、DPAPI 加密的密钥、自定义提示词 | `ModelSettings.Save` | 手动保存 |
| `%LOCALAPPDATA%\FolderScope\scan-cache\scan-<guid>.jsonl` | 当前扫描的文件明细（工作文件） | `FileDetailStore` | 扫描后移入历史目录 |
| `%LOCALAPPDATA%\FolderScope\scan-cache\scan-<guid>.jsonl.index.json` | 上者的目录索引 | `FileDetailStore.Finish` | 同上 |
| `%LOCALAPPDATA%\FolderScope\scan-cache\history\scan-<时间戳>-<guid>\snapshot.json` | 扫描快照 | `ScanHistory.Save` | **只保留最近 3 次** |
| `...\history\scan-...\details.jsonl` (+ `.index.json`) | 该次扫描的文件明细 | 同上 | 随历史一起淘汰 |
| `...\history\scan-...\overview-v1.json` | 首屏概览（≤ 4 MiB） | `ScanHistoryOverview.TryWrite` | 同上 |
| `%TEMP%\FolderScope\scan-cache\` | 明细落盘的备用位置 | `ScanWindow.StartScan` | 由系统清理 |
| `<程序目录>\scan-cache\` | 明细落盘的第三备用位置 | 同上 | 手动清理 |
| `<程序目录>\folderscope.settings.json` ⚠️ | 窗口尺寸、上次路径、筛选条件 | `DesktopSettings.Save` | 手动保存；**装在 Program Files 时会失败**（I-005） |
| `%LOCALAPPDATA%\FolderScope\watchdog-<pid>-<ticks>.log` | 看护进程事件 | `ResponsivenessWatchdog.Record` | 累积，可删 |
| `%LOCALAPPDATA%\FolderScope\lifecycle-<pid>.log` | 主进程启动/退出事件 | `Program.RecordLifecycle` | 累积，可删 |

**缓存位置选择顺序**（`StartScan`）：

- 扫描"整个电脑"：`%LOCALAPPDATA%` → `%TEMP%` → 程序目录
- 扫描单个路径：**程序目录** → `%LOCALAPPDATA%` → `%TEMP%`

任选其一后都会校验"不在被扫描目录内"，并在实际写入前用 `.probe` 文件验证可写。

---

## 6. 常见问题排查

| 症状 | 排查 |
|---|---|
| 构建报 `NETSDK1045` | 未装 .NET 10 SDK；`dotnet --list-sdks` 确认 |
| 启动弹框"请通过 FolderScope.Desktop.exe 启动" | 你是用 `dotnet run` 或重命名了 exe；直接跑 `bin\Release\...\FolderScope.Desktop.exe`（I-009） |
| 设置保存失败 | 程序目录不可写；见 I-005，或手动用「文件 → 保存当前设置」看具体错误 |
| 扫描结果全是 0 | 检查「快速估算 > N MiB」输入框；默认 1 MiB，小文件不计入。设为 0 统计全部 |
| 大目录扫描提前停止 | 看状态栏；可能触发节点上限（建议开启「文件明细落盘」）或内存预算（扫描菜单可调 2/4/8/16 GiB） |
| 历史只剩三条 | 设计如此，`ScanHistory.Save` 只保留最近 3 次 |
| AI 报"无法连接模型服务" | 检查网络/代理；Codex CLI 模式还会检查 `codex login status` |
| Codex 找不到 | 模型设置里填 `codex.exe` 完整路径；或用 CLI 模式（需 HTTPS API key） |
| 状态栏出现红色"无响应保护失效" | 看 `%LOCALAPPDATA%\FolderScope\watchdog-*.log`；软件已主动取消当前操作，建议保存后重启 |
| 想复现 180 秒强杀 | DEBUG 构建下「保护验证 → 验证持续无响应退出」会让 UI 线程卡 4 分钟 |

---

## 7. 开发约定速查

- 提交前：构建 4 个项目 + 跑模型测试与核心测试。
- 修改序列化结构 → 同步 `DATA-FORMATS.md`，并考虑旧快照兼容（`ReportReader` 有严格校验，改字段要加迁移）。
- 修改 `ScanNode` / `ScanReport` 字段 → 检查 `ReportReader` 的校验和 `FileDetailStore.ValidateFileRecord`。
- 新增 UI 控件 → 设 `AutomationProperties.SetName`；长列表开虚拟化。
- 不要提交 `*.jsonl`、`model-settings.json`、`folderscope.settings.json`、`bin/`、`obj/`、`research/`。
