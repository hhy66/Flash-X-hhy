# 问题文档

> 基线：`da32d6b`，2026-11-20。
> 状态：⛔ 未修 ｜ 🔄 修复中 ｜ ✅ 已修（保留记录，不删除）
> 优先级：**P0** 阻断交付 ｜ **P1** 高 ｜ **P2** 中 ｜ **P3** 低
>
> 规则：先记录，再修复。修复后保留本文条目，把状态改为 ✅ 并写明修复提交。

---

## 索引

| 编号 | 标题 | 优先级 | 状态 |
|---|---|---|---|
| [I-001](#i-001-核心测试套件的-csv-断言已失效) | 核心测试套件的 CSV 断言已失效 | **P0** | ✅ |
| [I-002](#i-002---audit-only-硬编码开发机路径不可移植) | `--audit-only` 硬编码开发机路径，不可移植 | **P0** | ✅ |
| [I-003](#i-003-扫描进度条永远不显示setprogress-参数被忽略) | 扫描进度条永远不显示（`SetProgress` 参数被忽略） | P2 | ⛔ |
| [I-004](#i-004-版本号硬编码在窗口标题) | 版本号硬编码在窗口标题 | P2 | ⛔ |
| [I-005](#i-005-桌面设置写在程序目录) | 桌面设置写在程序目录 | P2 | ⛔ |
| [I-006](#i-006-接手笔记位于-gitignore-范围内) | 接手笔记位于 `.gitignore` 范围内 | P1 | ✅ |
| [I-007](#i-007-没有-sln-文件) | 没有 `.sln` 文件 | P3 | ⛔ |
| [I-008](#i-008-complete-语义容易被误解) | `Complete` 语义容易被误解 | P3 | ⛔ |
| [I-009](#i-009-启动强依赖可执行文件名) | 启动强依赖可执行文件名 | P3 | ⛔ |
| [I-010](#i-010-主窗口单文件-1602-行) | 主窗口单文件 1,602 行 | P2 | ⛔ |
| [I-011](#i-011-无-cicd) | 无 CI/CD | P2 | ⛔ |
| [I-012](#i-012-品牌名与命名空间不一致) | 品牌名与命名空间不一致 | P3 | ⛔ |
| [I-013](#i-013-测试-harness-首错即停) | 测试 harness 首错即停 | P2 | ⛔ |
| [I-014](#i-014-仓库内-127-mb-截图) | 仓库内 1.27 MB 截图 | P3 | ⛔ |
| [I-015](#i-015-聊天复制按钮未处理剪贴板异常) | 聊天"复制"按钮未处理剪贴板异常 | P2 | ⛔ |
| [I-016](#i-016-扫描后的目录索引未传内存预算) | 扫描后的目录索引未传内存预算 | P3 | ⛔ |
| [I-017](#i-017-扫描后明细校验的异常类型缺失可能崩溃) | 扫描后明细校验的异常类型缺失，可能崩溃 | P1 | ⛔ |
| [I-018](#i-018-两处异常文案断言与实现不一致) | 两处异常文案断言与实现不一致 | P0 | ✅ |

---

## I-001 核心测试套件的 CSV 断言已失效

- **优先级**：P0　**状态**：✅ 已修（commit `7cd7cfa`）
- **位置**：`tests/FolderScope.Tests/Program.cs:404`（修复前）

### 现象

核心回归套件跑到第 68 项固定失败，进程退出，**其后的全部用例从未执行**。

### 复现

```powershell
$out = Join-Path $env:TEMP ('fs-core-' + [guid]::NewGuid().ToString('N'))
dotnet run --project tests/FolderScope.Tests/FolderScope.Tests.csproj -c Release -- $out
```

### 证据

```
PASS node safety limit returns marked partial result
Unhandled exception. System.Exception: FAILED: node-limited CSV marks every row scan incomplete
   at Program.<Main>$(String[] args) in ...\tests\FolderScope.Tests\Program.cs:line 404
```

### 根因

断言按"行尾 5 列"匹配：

```csharp
Check(File.ReadAllLines(limitedCsv).Skip(1)
    .All(line => line.EndsWith("\"False\",\"0\",\"True\",\"0\",\"0\"")), ...);
```

但 `ReportWriter.WriteCsv` 的列已扩到 **27 列**，末尾变成
`SizeState,KnownLogicalBytes,IsLowerBound`，旧的 5 列后缀不可能匹配。

### 影响

- 无法用测试证明任何改动安全——这是当前**最大的工程风险**。
- 上游 `chenlinzhe/Flash-X` 同样存在此问题，属项目遗留，不是本机环境问题。

### 建议改法

按**列名**定位而不是行尾子串：

```csharp
var lines = File.ReadAllLines(limitedCsv);
var header = lines[0].Split(',');
var completeIndex = Array.IndexOf(header, "ScanComplete");
Check(lines.Skip(1).All(line => line.Split(',')[completeIndex] == "False"), "…");
```

（注意 `CsvCell` 会给每格加引号，也可直接 `Trim('"')` 后比较。）

### 修复

三处按"行尾子串"匹配的断言全部改为**按列名定位**，并新增本地解析函数 `CsvCells`（状态机解析，正确处理引号包裹、`""` 转义和字段内逗号；路径含逗号也不会错位）：

- 原 `404`：按 `ScanComplete` 列断言每行 `False`；列缺失时断言直接失败，不会误判通过。
- 原 `502`：改为断言表头含 `ScanComplete` / `SizeState` / `IsLowerBound`，用 `Name == "a.bin"` 定位行比对 `CreatedUtc` 的 `"O"` 值。
- 原 `572`：用 `Id == "0"` 定位根行，按列名断言 `RetainsFileNodes=False`、`UnlistedFileBytes=500`、`UnlistedFileCount=250`。

修复过程中又暴露两处同类过期断言（异常文案），见 I-018，已一并处理。

### 验收结果

- 修复前：`FAILED: node-limited CSV marks every row scan incomplete`，通过 67 项。
- 修复后：`Completed 134 checks`，退出码 0，无 `FAILED`。
- 生产代码未改动，`ReportWriter` 行为不变。

---

## I-002 `--audit-only` 硬编码开发机路径，不可移植

- **优先级**：P0　**状态**：✅ 已修（commit `7cd7cfa`）
- **位置**：`tests/FolderScope.Tests/AuditChecks.cs:19`（修复前）

### 现象

在任何非上游作者机器上执行 `--audit-only` 必然抛异常，审计用例（20+ 项）完全无法运行。

### 复现

```powershell
dotnet run --project tests/FolderScope.Tests/FolderScope.Tests.csproj -c Release -- --audit-only
```

### 证据

```
Unhandled exception. System.InvalidOperationException:
Audit fixture must stay inside the authorized D: project.
   at AuditChecks.Run() in ...\AuditChecks.cs:line 20
```

### 根因

```csharp
var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
if (!project.Equals(@"D:\mypj5\nixiang\folder-size-ai", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Audit fixture must stay inside the authorized D: project.");
```

这是一条**安全围栏**（只允许在作者的项目目录内做文件操作），但把具体路径写死了，围栏变成了门槛。

### 影响

- 一半的回归覆盖（合盘、落盘、索引回退、取消、离线文件、快照预检）在本机无法验证。
- 无法进入 CI。

### 建议改法

把"项目根"改成参数，并保留围栏语义：

1. `--audit-only [<projectRoot>]`，缺省用 `AppContext.BaseDirectory` 向上找含 `src/FolderScope.Core` 的目录。
2. 围栏条件改为："scratch 必须是 `<projectRoot>/research/` 的直接子目录且不以重解析点形式存在"。
3. 失败时提示如何传参，而不是断言一个具体盘符。

### 修复

- 新增 `ResolveProjectRoot()`：优先 `FLASHSCOPE_AUDIT_ROOT`（并校验含 `src/FolderScope.Core`），否则从 `AppContext.BaseDirectory` 逐级上溯寻找 `src/FolderScope.Core/FolderScope.Core.csproj`；都失败时抛出带设置指引的中文错误。
- 新增两道前置围栏：临时目录必须是 `<项目根>/research` 的**直接**子目录；`research` 不得是重解析点。越界在创建任何文件之前抛异常。
- `finally` 清理条件未放宽（路径前缀 + `audit-tiny-` 前缀 + 非重解析点）。

### 验收结果

- `--audit-only` → `Audit checks: 39 passed, 0 failed`，退出码 0。
- `research/` 下无残留 `audit-tiny-*` 目录。

---

## I-003 扫描进度条永远不显示（`SetProgress` 参数被忽略）

- **优先级**：P2　**状态**：⛔
- **位置**：`src/FolderScope.Desktop/Program.cs:1078`

### 现象

扫描、加载快照、导出报告时进度条始终不可见，状态栏只在最底部留出一块空白。

### 根因

```csharp
private void SetProgress(bool running)
{
    _progress.IsIndeterminate = false;
    _progress.Value = 0;
    _progress.Visibility = Visibility.Collapsed;   // 无条件隐藏，running 从未被读取
}
```

`_progress` 初始 `Visibility.Hidden`、`Height = 8`。所有调用点（`StartScan`、`LoadReport`、`ExportReport`）都传了 `true`/`false`，但方法体忽略了参数。

### 影响

长扫描/大快照加载期间，用户只能看到状态文字变化，没有"正在进行"的视觉反馈；取消按钮成为唯一反馈。

### 建议改法（二选一）

- **A（推荐）**：真正接上。扫描时用 `ScanProgress.Entries` 做不确定进度（`IsIndeterminate = true`），加载快照时同样；结束置 `Collapsed`。
- **B**：若确定不需要进度条，删除 `_progress` 字段、`SetProgress` 方法及全部调用，避免误导。

### 验收标准

- 扫描期间 `_progress.Visibility == Visible`；扫描结束后 `Collapsed`。
- 或代码中不再存在 `SetProgress` 与未使用的 `_progress`。

---

## I-004 版本号硬编码在窗口标题

- **优先级**：P2　**状态**：⛔
- **位置**：`src/FolderScope.Desktop/Program.cs:144`

### 现象

窗口标题 `Flash-X v2026.11.16.8 · 磁盘空间分析` 中的版本号是字面量，与 `.csproj` / 程序集版本无关联；README 与发布包无法据此判断版本。

### 影响

- 改版本需要改源码并重新构建，容易遗漏。
- 无法用"关于"信息排查用户实际使用的版本。

### 建议改法

1. `FolderScope.Desktop.csproj` 增加 `<Version>2026.11.16.8</Version>`。
2. 标题改为从 `AssemblyInformationalVersionAttribute` 读取：

```csharp
var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
Title = $"Flash-X v{version.Split('+')[0]} · 磁盘空间分析";
```

### 验收标准

- 只改 `.csproj` 版本号，重新构建后窗口标题同步变化。

---

## I-005 桌面设置写在程序目录

- **优先级**：P2　**状态**：⛔
- **位置**：`src/FolderScope.Desktop/DesktopSettings.cs:18`

### 现象

```csharp
public static string FilePath => Path.Combine(AppContext.BaseDirectory, "folderscope.settings.json");
```

窗口尺寸、上次扫描路径、筛选条件写在**可执行文件同目录**。

### 影响

- 安装到 `C:\Program Files\` 时普通用户无写权限 → 设置静默保存失败（状态栏提示"设置未保存"）。
- 多用户共用一台机器时互相覆盖。
- 与 `%LOCALAPPDATA%\FolderScope\` 下的模型设置、扫描历史**分散在两处**，备份/清理不便。

### 建议改法

迁移到 `%LOCALAPPDATA%\FolderScope\desktop-settings.json`，并在 `Load()` 里做一次性回退：优先新路径，不存在时读旧路径并写回新路径（保留旧文件不删）。

### 验收标准

- 通过只读安装目录启动后仍能保存设置。
- 旧路径存在时自动迁移且不丢数据。

---

## I-006 接手笔记位于 `.gitignore` 范围内

- **优先级**：P1　**状态**：✅（本次已迁入 `docs/`）
- **位置**：`.gitignore:7`（`**/research/`）与 `research/install-notes.md`

### 现象

上游把整个 `research/` 目录忽略。本 fork 的首次接手笔记恰好写在那里，**不会进入版本库**，换机器或新会话即丢失。

### 处置

本次已将其中有长期价值的内容迁入：
- 环境/构建/运行 → `docs/DEVELOPMENT.md`
- 已知问题与证据 → 本文 I-001、I-002
- 版本与实测数据 → `docs/PROGRESS.md`

`research/` 继续作为**本机临时目录**使用，不作为文档载体。

### 遗留动作

后续如需保存测量数据，放 `docs/measurements/`（需在 `.gitignore` 中确认不被误伤），不要放 `research/`。

---

## I-007 没有 `.sln` 文件

- **优先级**：P3　**状态**：⛔

### 现象

仓库没有解决方案文件，`dotnet build` 必须逐个指定 `.csproj`；Visual Studio 打开需要手动添加项目。

### 影响

新接手者与 CI 都得维护项目列表；漏建某个测试项目不会有任何提示。

### 建议改法

`dotnet new sln -n Flash-X` 后添加 5 个项目（4 个源码 + 2 个测试）。注意测试项目目标框架不同（`net10.0` 与 `net10.0-windows`），`dotnet build Flash-X.sln` 需在 Windows 上执行。

### 验收标准

`dotnet build Flash-X.sln -c Release` 一次构建全部，0 warning / 0 error。

---

## I-008 `Complete` 语义容易被误解

- **优先级**：P3　**状态**：⛔（建议文档化，不一定改代码）
- **位置**：`src/FolderScope.Core/Scanner.cs:96`

### 现象

```csharp
public bool Complete => !Cancelled && UnlistedSkippedLinks == 0
    && Issues.Count == 0 && SuppressedIssueCount == 0
    && Nodes.All(n => !n.LinkSkipped && !n.Incomplete);
```

`Complete == false` 有两种完全不同的原因：
1. 有内容**没读到**（拒绝访问、取消、上限）——大小不可信；
2. 逻辑字节**已完整**，只是"实际占用"读取失败（`allocated-space` issue）。

### 影响

- UI 会显示"扫描完成，部分项未读取"，但此时逻辑大小其实完整。
- 处理 `Complete` 的代码容易误判"数据不可用"。

### 建议

- 短期：在 `ARCHITECTURE.md` / `CLAUDE.md` 中明确该语义（已写入）。
- 中期（可选）：增加独立的 `LogicalComplete` 属性，仅在"影响逻辑字节"的原因上为 false，保留 `Complete` 兼容快照校验。

### 验收标准

若新增属性：快照往返、UI 状态栏、Markdown 摘要三处都能区分"逻辑完整但占用缺失"。

---

## I-009 启动强依赖可执行文件名

- **优先级**：P3　**状态**：⛔
- **位置**：`src/FolderScope.Desktop/ResponsivenessWatchdog.cs:18-20`、`Program.cs:38-40`

### 现象

```csharp
if (executable is null || !executable.EndsWith("FolderScope.Desktop.exe", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("请通过 FolderScope.Desktop.exe 启动…");
```

启动失败时 `Program.Main` 弹框后 `return`，**整个程序不启动**。

### 影响

产品已改名 Flash-X，一旦把程序集名/发布文件名改成 `Flash-X.exe`（很自然的诉求），程序直接无法启动。

### 建议改法

- 判定改为"可执行文件在自身目录且进程名与 `Environment.ProcessPath` 一致"，不再绑定 `FolderScope.Desktop.exe`。
- 或允许 `--no-watchdog` 降级启动，只显示警告而不阻塞主流程。

### 验收标准

把发布产物重命名为 `Flash-X.exe` 后仍能正常启动并启用看护。

---

## I-010 主窗口单文件 1,602 行

- **优先级**：P2　**状态**：⛔
- **位置**：`src/FolderScope.Desktop/Program.cs`

### 现象

`ScanWindow` 的字段、构造函数、扫描、加载、历史预览、筛选、导出、看护、分页、两个内部进度适配器全部在一个文件里。

### 影响

- 冲突概率高、审查困难、`Program.cs` 任何改动都要重读上下文。
- 违反本仓库 `CODING-STANDARDS.md` §2.3 的 400 行目标（当前为唯一豁免）。

### 建议改法（按收益排序，逐步做，不要一次大爆炸）

1. 抽出 `ScanWindow.Export.cs`（`ExportReport`、`ExportChart`、`AnalysisReportWriter` 调用）。
2. 抽出 `ScanWindow.History.cs`（`ScanHistoryOverview` 相关 4 个方法）。
3. 抽出 `ScanWindow.Filters.cs`（`ConfigureNameFilter`、`ConfigureAdvancedFilter`、`DescribeFilter`）。
4. 抽出 `ScanWindow.Reports.cs`（`ShowReport`、`ShowIssues`）。
5. 最后若仍超 800 行，把 `LiveScanPreview` / `ThrottledScanProgress` / `NodeRow` 提到独立文件。

全部用 `public sealed partial class ScanWindow`，**不改任何行为与字段可见性**。

### 验收标准

- 每步提交后 `dotnet build` 0 warning，ModelTests 的 UI 交互用例仍通过。
- 最终 `Program.cs` ≤ 600 行，其它 partial 文件 ≤ 400 行。

---

## I-011 无 CI/CD

- **优先级**：P2　**状态**：⛔

### 现象

仓库没有任何 CI 配置。I-001 这种红灯可以长期存在而无人发现。

### 建议改法

GitHub Actions，`windows-latest` + `actions/setup-dotnet@v4`（`dotnet-version: 10.0.x`）：

```yaml
- run: dotnet build src/FolderScope.Desktop/FolderScope.Desktop.csproj -c Release
- run: dotnet build src/FolderScope.Cli/FolderScope.Cli.csproj -c Release
- run: dotnet run --project tests/FolderScope.ModelTests/FolderScope.ModelTests.csproj -c Release
- run: dotnet run --project tests/FolderScope.Tests/FolderScope.Tests.csproj -c Release -- ${{ runner.temp }}/fscope
```

`--audit-only` 在 I-002 修复后再加入。

### 验收标准

- PR 上能看到构建 + 测试结果。
- 失败时日志能直接定位到 `Check` 标签。

---

## I-012 品牌名与命名空间不一致

- **优先级**：P3　**状态**：⛔（已知且有意保留）

### 现象

产品名 `Flash-X`，程序集 `FolderScope.*`，UI 标题 `Flash-X`，设置目录 `%LOCALAPPDATA%\FolderScope`，导出前缀 `ff-`，Markdown 标题 `# Flash-X AI 分析摘要`。

### 影响

- 新读者会疑惑两个名字的关系。
- 若将来真要统一，是一次跨 46 个文件 + 用户数据目录的破坏性迁移。

### 处置建议

**暂不改**。在 `CLAUDE.md` 与本节记录"命名空间是历史遗留，产品名是 Flash-X"。
若确需改名，走独立里程碑，并保留对旧设置/历史目录的读取兼容。

### 验收标准

不适用（决策项）。

---

## I-013 测试 harness 首错即停

- **优先级**：P2　**状态**：⛔
- **位置**：`tests/FolderScope.Tests/Program.cs:145`

### 现象

```csharp
void Check(bool condition, string name)
{ if (!condition) throw new Exception("FAILED: " + name); checks++; ... }
```

第一个失败就抛异常终止进程，一次只能看到一个失败。

### 影响

I-001 这种"一个断言坏掉导致 52 项未知"的情况会反复发生；修 bug 的迭代成本高。

### 建议改法

- `Check` 记录失败清单而不是抛出；主流程末尾统一汇总并 `Environment.ExitCode = 1`。
- 但**前置条件类**检查（如"fixture 创建失败"）仍需立即抛出。
- 与 `AuditChecks` 的 `failures` 列表风格统一（那里已经是汇总式）。

### 验收标准

一次运行能列出全部失败标签；退出码在有任何失败时为非 0。

---

## I-014 仓库内 1.27 MB 截图

- **优先级**：P3　**状态**：⛔
- **位置**：`docs/images/ff-desktop.png`（1,274.90 KB）

### 影响

克隆体积、每次改动图片都会在历史里留下大对象。

### 建议改法

压缩为 WebP/优化 PNG（目标 < 300 KB），或移到 Git LFS / 外部图床并在 README 引用。注意 README 已引用该路径，改格式需同步。

### 验收标准

图片 < 300 KB，README 正常显示。

---

## I-015 聊天"复制"按钮未处理剪贴板异常

- **优先级**：P2　**状态**：⛔
- **位置**：`src/FolderScope.Desktop/AnalysisChat.cs:162`

### 现象

```csharp
copy.Click += (_, _) => { Clipboard.SetText(text); copy.Content = "已复制"; };
```

`Clipboard.SetText` 在剪贴板被其它进程占用时会抛 `System.Runtime.InteropServices.ExternalException`。这是 `async void` 之外的事件处理器，异常将向上冒泡到 WPF 分发器，可能直接终止程序。

### 对比

同一仓库的 `ScanWindow.ShowIssues` 已经正确处理：

```csharp
try { Clipboard.SetText(text.ToString()); ... }
catch (System.Runtime.InteropServices.ExternalException ex) { summary.Text = "剪贴板暂不可用：" + ex.Message; }
```

### 建议改法

照抄 `ShowIssues` 的处理方式：捕获 `ExternalException`，在状态行提示，按钮文案不变。

### 验收标准

占用剪贴板时点击"复制"不崩溃，出现中文提示。

---

## I-016 扫描后的目录索引未传内存预算

- **优先级**：P3　**状态**：⛔
- **位置**：`src/FolderScope.Desktop/Program.cs:562`（对比 `:1332`）

### 现象

扫描路径：

```csharp
var index = DirectoryIndex.Create(report, sortChildren: !report.Cancelled, sortCancellationToken: token);
```

加载路径：

```csharp
var index = DirectoryIndex.Create(report, cancellationToken: token, sortChildren: !report.Cancelled, maximumManagedBytes: memoryBudgetBytes);
```

只有加载路径传了 `maximumManagedBytes`，扫描后建索引不受预算约束。

### 影响

扫描本身虽已按预算停止，但随后建索引仍可能把托管内存推到预算之上（尤其是 `maximumNodes` 很大或明细落盘模式）。属于"保护不彻底"，不是崩溃。

### 建议改法

扫描路径同样传 `maximumManagedBytes: memoryBudgetBytes`；捕获 `InvalidDataException` 并提示"索引超出内存预算"（此时仍可保留扫描结果，只是无法展开树）。

### 验收标准

在 2 GiB 预算下扫描超大目录，不会只因建索引而 OOM；失败时给出可读提示。

---

## I-017 扫描后明细校验的异常类型缺失，可能崩溃

- **优先级**：P1　**状态**：⛔
- **位置**：`src/FolderScope.Desktop/Program.cs:564-573`、`:607`

### 现象

扫描完成后校验明细关联：

```csharp
try { FileDetailStore.ValidateAssociation(detailPath, report.FileDetailsStoreId); ... }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
{ detailStatus = " · 文件明细不可用：" + ex.Message; }
```

`ValidateAssociation` 在文件头 schema/StoreId 异常时抛的是 **`InvalidDataException`**，不在此过滤列表中；外层 `StartScan` 的捕获列表（`:607`）也不含 `InvalidDataException`。

### 后果

该异常从 `async void` 的 `_scan.Click` 事件处理器逃逸 → 未观察的异常 → WPF 进程终止。触发条件：明细文件写入被截断/损坏，或旧文件被替换（概率低，但正是该校验要防的场景）。

### 对比

`LoadReport` 的同类捕获**包含** `InvalidDataException`（`:1342`、`:1383`），说明这是扫描路径的遗漏，不是设计选择。

### 建议改法

1. `:571` 的过滤条件加入 `or InvalidDataException`。
2. `:607` 的外层 catch 加入 `InvalidDataException` 与 `JsonException`（与 `LoadReport` 对齐）。
3. （可选，更稳）所有 `async void` 事件入口统一包一层 `try/catch`，把未预期异常写入生命周期日志并弹提示，而不是让进程消失。

### 验收标准

- 人为构造损坏的 `details.jsonl` 后扫描，界面显示"文件明细不可用"，程序不退出。
- `StartScan` 的捕获类型集合与 `LoadReport` 一致。

---

## I-018 两处异常文案断言与实现不一致

- **优先级**：P0　**状态**：✅ 已修（commit `7cd7cfa`，与 I-001 同批）
- **位置**：`tests/FolderScope.Tests/Program.cs:507`、`:518`（修复前行号）

### 现象

I-001 修好后套件继续执行，第 91 项失败：

```
FAILED: snapshot node cap rejects before object deserialization
```

断言用 `ex.Message.Contains("node safety limit")`，而 `SnapshotPreflight` 实际抛的是 `"Snapshot node limit exceeded."`。
同类的下一项 `oversized snapshot rejected before JSON parsing` 期望 `"safety limit"`，实际是中文消息 `"快照超过当前内存预算允许的加载大小，原始结果仍保留。"`。

### 根因

`node safety limit` 是**扫描器**（`DirectoryScanner` / `ComputerScanner`）的消息；**快照预检**用了另一套英文措辞，断言把两条不同的消息混为一谈。体积上限的断言则假设消息是英文，实际早已本地化。

### 影响

这两项在 I-001 之后永远不会被执行到，因此长期无人发现——正是"首错即停"掩盖问题的典型（见 I-013）。

### 修复

- 节点上限：改为 `ex.Message.Contains("node limit")`，与 `"Snapshot node limit exceeded."` 对齐。
- 体积上限：改为只断言异常类型 `InvalidDataException`，并加注释说明理由——该文件是 128 MiB 零填充内容，若真的进入 JSON 解析会抛 `JsonException`，因此捕获到 `InvalidDataException` 即证明体积闸门在解析前生效。

### 验收结果

- 核心套件 `Completed 134 checks`，退出码 0。
