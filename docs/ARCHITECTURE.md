# 架构说明

## 1. 总体形状

```
Flash-X
├─ FolderScope.Core     net10.0          纯逻辑，可被 CLI / Desktop / 测试复用
├─ FolderScope.Cli      net10.0   exe    无头扫描，退出码即状态
├─ FolderScope.Desktop  net10.0-windows  WPF 主程序（WinExe）
└─ tests/
   ├─ FolderScope.Tests       net10.0         核心回归（console harness）
   └─ FolderScope.ModelTests  net10.0-windows 模型配置 + WPF 交互
```

依赖方向严格单向：`Desktop → Core`、`Cli → Core`、`Tests → Core`、`ModelTests → Desktop`。
`Core` 不引用任何 UI 类型；只有 `FileSpaceReader` / `AllocatedSpace` 两处 P/Invoke，且第一行就 `OperatingSystem.IsWindows()` 守卫。

没有 `.sln`，`dotnet build` 需指定项目文件。没有 NuGet 依赖，离线可构建。

---

## 2. 数据流

```
                 ┌──────────────┐
 目录/盘符 ──────▶│ DirectoryScanner │──▶ ScanReport (Nodes[] + Issues[])
 整个电脑 ──────▶│ ComputerScanner  │        │
                 └──────────────┘        │
                          │ 可选           ├─▶ FileDetailStore  (details.jsonl + .index.json)
                          ▼                ├─▶ ReportWriter     (JSON / CSV / XML)
                   ScanPreview              ├─▶ AnalysisReportWriter (Markdown)
                   ScanProgress             └─▶ ScanHistory.Save (最近 3 次)
                                                   │
                                                   ▼
                                            ScanHistoryOverview  (overview-v1.json，快速首屏)
                                                   │
 快照 JSON ──────▶ ReportReader ──▶ ScanReport ──▶ DirectoryIndex ──▶ UI (TreeView / DataGrid / SizeChart)
                                                   │
                                                   └─▶ ReportQuery / NodeFilter ──▶ Top-N 与筛选
```

三条互不干扰的"视图通道"：

1. **完整结果**：`ScanReport.Nodes` 全在内存（或目录汇总模式只留目录节点）。
2. **文件明细**：只在需要时读 `details.jsonl`，按 `parentId` 分页，不占内存。
3. **首屏概览**：`overview-v1.json` 是快照的"前 100 项"摘要，先渲染它，再后台加载完整快照。

---

## 3. 扫描算法（`DirectoryScanner.Scan`）

**迭代式 DFS，显式栈**，不使用递归——深目录树不会栈溢出。

```
pending: Stack<(DirectoryInfo, int id)>  ← 只有待展开的目录
while pending 非空:
    pop (folder, id)
    foreach entry in folder.EnumerateFileSystemInfos():
        ├─ 命中快速估算门槛 → EstimateExcludedFiles++，跳过
        ├─ 达到 nodeLimit / 内存预算 → 抛内部异常，统一捕获并标记
        ├─ 目录汇总模式 + FileInfo → 累计到父节点的 UnlistedFile*（或写 FileDetailStore）
        └─ 否则 NewNode() → Nodes.Add() → 若为目录则压栈
```

关键点：

| 机制 | 实现 | 为什么 |
|---|---|---|
| 节点编号 | `Id = Nodes.Count`，父 id 恒小于子 id | 聚合可用一次倒序循环，无需递归 |
| 大小聚合 | 扫描结束倒序遍历 `for (i = Count-1; i > 0; i--)` 累加到父 | O(n)、无递归、可 `checked` 溢出保护 |
| 重解析点 | 任何 `ReparsePoint` 都跳过并标 `LinkSkipped` | 防目录环、防触发云端文件下载 |
| 硬链接去重 | `FileIdentity(Volume, Low, High)` + 进程内 `HashSet` | "占用空间"按数据块计一次，归首个路径 |
| 快速估算 | `minimumFileBytes`，严格大于才计入 | 500G 盘秒级定位大项 |
| 节点上限 | 默认 1000 万（有明细落盘时不限） | 防内存爆炸，落盘模式可突破 |
| 内存预算 | `GC.GetTotalMemory(false)` 定期检查 | 面向普通用户机器的保护线 |
| 取消 | 每 80ms 或每 100 项检查一次令牌 | 取消后保留已读结果，标记 partial |
| 进度 | `ScanProgress(已访问项数, 当前路径)` | UI 线程只取最新值，不排队 |

**错误不中断扫描**：文件系统异常被 `ScanDiagnostics.ReasonFor` 分类为 `AccessDenied` / `ReadError`，记 `ScanIssue`，节点标 `Incomplete`，父节点原因位向上传播。只有 `DetailWriteException`、节点上限、内存上限、取消会提前终止。

`ComputerScanner` 在 `DirectoryScanner` 之上做**串行合盘**：逐盘扫描 → `MergeDrive` 把子 `ScanReport` 的节点 id 平移到全局偏移 → 汇总到合成根节点（`Path = "shell:MyComputerFolder"`，`Name = "整个电脑"`）。盘符根节点的 `Name` 被替换为盘符本身。

---

## 4. 大小语义（全项目最容易出错的地方）

唯一判定入口 `NodeSizeStatus.Get(node)`：

| 状态 | 条件 | 显示 | 可排序 |
|---|---|---|---|
| `Known` | 未跳过、未不完整 | `1.23 GiB` | 是 |
| `Partial` | 不完整且 `TotalBytes > 0` | `1.23 GiB（部分）`，语义是"≥" | 是（下界） |
| `Unknown` | 不完整且 `TotalBytes == 0` | 未知 + 原因 | **否**（`null`，排最后） |
| `SkippedLink` | 重解析点 | 未知（已跳过） | **否** |

推论，必须记住：

- `Unknown` 不能当 0 参与百分比或"最小大小"筛选，否则会把"没读到"说成"空"。
- `Partial` 不能当上界参与 `MaximumBytes` 筛选（可能漏掉实际更大的项）。
- 目录大小是累计值，父子相加会重复计算。
- 快速估算模式下，目录的 0 B 意思是"没有超过门槛的文件"，不是空目录。

---

## 5. 快照加载与校验（`ReportReader`）

三层防线，任何一层不过直接 `InvalidDataException`：

1. **体积/内存闸门**：文件 > `maximumSnapshotBytes`（默认 128 MiB）直接拒绝；读写过程持续检查托管内存预算。
2. **`SnapshotPreflight`**：用 `Utf8JsonReader` 流式扫一遍，**不建 DOM**。校验根对象、`Schema == folderscope.scan.v1`、`Nodes`/`Issues` 是数组、节点数上限、单 token ≤ 1 MiB、无重复信封属性。缓冲从 64 KiB 起倍增。
3. **全量语义校验**：反序列化后逐节点检查 id 连续性、父子关系非环、非负值、`HardLinkDuplicate` 与占用值的一致性；再用**一个复用缓冲区**重算 bytes/files/directories 三项聚合，与声明值比对。

校验通过后做**路径压缩**：文件节点若路径恰好等于 `父路径 + 分隔符 + Name`，只存目录路径（`ShareDirectoryPath`），`Path` 属性按需拼接。这是内存优化的关键，不要改成每节点存全路径。

---

## 6. 文件明细存储（`FileDetailStore`）

append-only JSONL，首行是信封：

```json
{"DetailSchema":"folderscope.details.v1","StoreId":"<guid>"}
```

后续每行一个 `ScanNode`（文件）。旁路 `.index.json` 记录"每个 parentId 对应的字节区间列表 + SHA-256 摘要"。

读取策略：

- 索引存在且 `Length` / `LastWriteTicks` / 摘要 / 区间连续性全部成立 → 按区间 `Seek` 读取（只读需要的目录）。
- 索引损坏、缺失、版本旧、区间有洞 → **静默回退到全量顺序扫描**。这条"回退但不丢数据"的性质有专门的回归测试。
- `StoreId` 不匹配 → 直接拒绝，避免把另一次扫描的明细贴到当前结果上。

写入失败只影响索引（`IndexWriteError`），正文仍然可顺序读取，扫描不会因此失败。

---

## 7. 桌面端结构

`ScanWindow` 是一个用代码手工搭建的 WPF 窗口（**无 XAML 文件**）：

```
DockPanel
├─ 菜单行（文件 / 扫描 / 查看 / 报表 + 设置齿轮）
├─ 顶部（防护警告 + 扫描目标行 + 筛选行 + 摘要）
├─ footer（状态行，可点击打开问题明细 + 进度条）
└─ Grid（左：历史下拉 + 目录树 | 分隔条 | 右：列表 + 分隔条 + AI 对话 + 折叠图表）
```

分页与虚拟化：

| 区域 | 页大小 | 说明 |
|---|---|---|
| 目录树 | 2000 | 展开时惰性填充，带"上一页/下一页子目录" |
| 列表 | 10000 | 一次性绑 `ItemsSource`，DataGrid 行/列虚拟化 |
| 文件明细 | 1000 | 从 `details.jsonl` 按需读 |
| 概览首屏 | 100 / 256 / 24 | 根 / 盘符 / 目录三级 |

后台任务三件套，靠"请求序号 + 引用相等"防止过期结果覆盖新状态：

```csharp
var request = ++_browseRequest;            // 或 _chartRequest / ++_chartRequest
var summary = await Task.Run(...);
if (request != _browseRequest || !ReferenceEquals(report, _report)) return;  // 丢弃过期结果
```

`_busy` 是全局互斥：扫描、加载、导出、全局报表、文件明细读取互斥，互斥期间相应按钮禁用。

---

## 8. AI 子系统

```
AnalysisChat ──▶ ModelSettings.Load() ──▶ EffectiveProfile
                     │
                     ├─ ClientType == "codex-cli"  ──▶ LocalCodexClient (子进程)
                     └─ 其它                       ──▶ TextModelClient (HTTPS)
```

支持三种 HTTP 协议：`openai-chat`（默认）、`openai-responses`、`ant-messages`（Anthropic）。
`ModelProfile.Endpoint()` 把 baseUrl 与协议对应的路由拼接（`/chat/completions`、`/responses`、`/messages`），并强制 HTTPS、拒绝 query/fragment/userinfo。

**模型配置模型**：一个"厂商"（`ProviderId`）下多个"模型"（`ModelProfile`），密钥按厂商共享（`ModelGrouping.ShareKeys`）。预设来自嵌入资源 `ModelPresets.json`（12 个厂商、99 个预置模型 + 1 个 Codex CLI 条目），用户配置合并覆盖预设。

**密钥**：`CryptProtectData` / `CryptUnprotectData`（DPAPI，当前用户作用域）→ Base64 存 JSON。解密后立即 `ZeroMemory`。失败消息刻意过滤原始 CLI 输出，防止账号/凭据泄漏到界面。

**上下文**：每次发送 = 系统提示词 + 当前目标的扫描摘要 + 最近 12 轮对话。响应上限 1800 tokens，HTTP 超时 85 秒，Codex 子进程超时 3 分钟、stdout/stderr 各限 4 MiB。

---

## 9. 稳定性设计

**无响应看护**：主程序启动时以 `--watch-responsiveness <pid> <startTicks>` 拉起自身副本作为看护进程。

- 看护进程启动时先核对 pid、启动时间与主模块路径三项身份，之后每 2 秒检查一次 `Process.Responding`；**连续 180 秒无响应**才强杀，杀前再复核一次启动时间与响应状态。
- 主进程侧的 supervisor 监控看护进程存活，退出则最多重启 3 次；3 次耗尽后 UI 顶部显示红色警告并取消当前操作。
- 计时用 `UnresponsiveDeadline` 累计"连续无响应"时长，恢复即清零；不依赖墙钟，改系统时间不会误杀。

**原子写入**：所有落盘（快照、设置、概览、索引）都是"同级临时文件 → `File.Move`"。`ReportWriter` 用可取消的 `FileStream` 子类，取消后连临时文件都不留。

**限额表**：

| 项 | 默认值 | 位置 |
|---|---|---|
| 扫描节点上限 | 10,000,000（落盘模式不限） | `DirectoryScanner.Scan` |
| 托管内存预算 | 8 GiB（UI 可选 2/4/8/16） | 同上 |
| 问题明细保留 | 10,000 条，其余只计数 | `ScanReport.AddIssue` |
| 快照大小 | 128 MiB / 内存预算的一半 | `ReportReader.Read` |
| 明细索引 | 8 MiB | `FileDetailStore.ReadDirectoryLines` |
| 概览文件 | 4 MiB | `ScanHistoryOverview` |
| 名称规则 | ≤ 64 条、每条 ≤ 256 字符、单次匹配 ≤ 100 ms | `NameFilter` |
| AI 响应 | 1800 tokens / 4 MiB | `TextModelClient`、`LocalCodexClient` |

---

## 10. 设计取舍记录

| 决策 | 原因 | 代价 |
|---|---|---|
| 不用 NuGet | 离线可用、供应链面为零 | 无现成 MVVM/日志/JSON 库 |
| 自研 console 测试而非 xUnit | 零依赖、可直接跑端到端场景 | 首错即停、无并行、无覆盖率 |
| UI 全代码构建 | 单文件可见全部布局、避免 XAML 生成步骤 | `Program.cs` 1,602 行 |
| 迭代式扫描 | 深目录不爆栈 | 代码比递归难读 |
| 明细落盘而非内存 | 数千万文件也能扫 | 多一套索引/回退逻辑 |
| 快照严格校验 | 损坏结果不能伪装成正常结果 | 加载慢、改格式成本高 |
| 概览文件重复存节点 | 首屏秒开 | 同一数据两份 |
| AI 只做建议 | 误删不可逆 | 用户需手动执行清理 |
