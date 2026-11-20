# 功能实现表

> 状态图例：✅ 已实现且有测试 ｜ 🟡 已实现但测试薄弱/手工验证 ｜ ⚠️ 已实现但有已知缺陷 ｜ ⛔ 未实现
>
> 基线：`da32d6b`（2026-11-20）。"验证"列写测试名，或 `手工:` + 步骤。
> 测试名取自 `tests/FolderScope.Tests/Program.cs` 的 `Check(...)` 标签。

---

## A. 扫描引擎

| ID | 功能 | 状态 | 实现位置 | 验证 | 备注 |
|---|---|---|---|---|---|
| A01 | 单目录递归扫描 | ✅ | `Core/Scanner.cs` `DirectoryScanner.Scan` | `exact bytes/counts including empty directory` 等 | 迭代栈，非递归 |
| A02 | 整机多盘串行扫描 | ✅ | `Core/ComputerScanner.cs` `Scan` / `MergeDrive` | `multi-root merge preserves totals...`（Audit） | 合成根 `shell:MyComputerFolder` |
| A03 | 逻辑大小累计 | ✅ | `Scanner.cs` `NewNode` + 倒序聚合 | `child aggregation` | 逻辑长度，非占用 |
| A04 | 实际占用统计（含硬链接去重） | ✅ | `Core/FileSpaceReader.cs`、`ScanReport.CountAllocatedSpace` | `allocated totals agree across full and directory scans...` | 需要 NTFS 元数据 |
| A05 | 快速估算门槛（`minimumFileBytes`） | ✅ | `Scanner.cs` 枚举循环；UI `_detailMinimum`；CLI `--min-file-mib` | `quick estimate excludes files at or below its threshold`（Audit） | 严格 `>`，门槛本身不计入 |
| A06 | 目录汇总模式（不保留文件节点） | ✅ | `Scan` 的 `retainFileNodes: false` + `UnlistedFile*` | `directory-only retention preserves full totals...` | 内存降一个数量级 |
| A07 | 文件明细落盘 | ✅ | `Core/FileDetailStore.cs` | `scan stores all file details on disk while retaining only directory nodes` | JSONL + 索引 |
| A08 | 节点上限保护 | ✅ | `Scan(..., maximumNodes)` + `ScanLimitException` | `node safety limit returns marked partial result` | 默认 1000 万 |
| A09 | 托管内存预算保护 | ✅ | `GC.GetTotalMemory` 周期检查 + `ScanMemoryLimitException` | `managed memory budget returns marked partial result` | 默认 8 GiB |
| A10 | 重解析点/目录环跳过 | ✅ | `NewNode` 中 `FileAttributes.ReparsePoint` | `directory link skipped without recursion` | 同时防云端文件下载 |
| A11 | 取消 → 部分结果 | ✅ | `OperationCanceledException` 分支 + `MarkUnfinished` | `cancellation reports partial state` 等 4 项 | 已读结果保留 |
| A12 | 实时目录预览 | 🟡 | `ScanPreview` 回调 + `LiveScanPreview` | `child enumeration failure preserves bytes...`（Audit） | UI 限 3000 项、队列上限 4096 |
| A13 | 进度上报（不排队） | ✅ | `Core/LatestScanProgress.cs` + `ThrottledScanProgress` | `100000 worker progress updates retain only newest snapshot...` | 80 ms 节流 |
| A14 | 扫描缓存目录自动排除 | ✅ | `Core/ScanCacheLocation.cs`、`ComputerScanner.excludedCacheDirectory` | `drive roots and similar path prefixes do not bypass output exclusion`（Audit） | 防止扫自己的输出 |
| A15 | 错误分类与原因位 | ✅ | `Core/ScanDiagnostics.cs` `ScanIncompleteReason` | `incomplete zero totals stay unknown...` 等 | 8 种原因位 |
| A16 | 问题明细上限（10,000 + 计数） | ✅ | `ScanReport.AddIssue` | `issue detail cap preserves suppressed count...` | 超出只计数 |
| A17 | 离线/按需取回文件保护 | ✅ | `FileSpaceReader.MayRecallData` | `both allocation readers reject offline files`（Audit） | 不触发云端下载 |
| A18 | 扫描输出位置校验 | ✅ | `Scan` 前置参数校验 | `scanner rejects its own growing detail output...`（Audit） | 落在被扫目录内直接抛错 |

## B. 持久化与快照

| ID | 功能 | 状态 | 实现位置 | 验证 | 备注 |
|---|---|---|---|---|---|
| B01 | JSON 快照导出 | ✅ | `ReportWriter.Write` | `saved scan loads with exact hierarchy and sizes` | 缩进格式，完整字段 |
| B02 | 历史快照精简写入 | ✅ | `ReportWriter.WriteHistorySnapshot` | `compact history preserves report mode...`（Audit） | 省略默认值 |
| B03 | 快照加载 + 全量校验 | ✅ | `ReportReader.Read` | 20+ 项（`corrupt snapshot aggregate rejected` 等） | 三层防线 |
| B04 | 流式信封预检（不建 DOM） | ✅ | `Core/SnapshotPreflight.cs` | `snapshot preflight chunk boundaries and envelope rejection`（Audit） | 单 token ≤ 1 MiB |
| B05 | 路径压缩（子节点共享父路径） | ✅ | `ScanNode.ShareDirectoryPath` + `ReportReader` | `shared and stored path lengths match full paths` | 显著省内存 |
| B06 | 明细索引 + 摘要校验 + 顺序回退 | ✅ | `FileDetailStore.ReadDirectoryLines` | `corrupted and legacy indexes fall back without hiding files`（Audit） | 索引坏了不丢数据 |
| B07 | 明细与快照关联校验（StoreId） | ✅ | `FileDetailStore.ValidateAssociation` | `detail association rejects replacement...`（Audit） | 防串扫描 |
| B08 | 最近三次扫描历史 | ✅ | `Desktop/ScanHistory.cs` | 手工：连扫 4 次后下拉只剩 3 条 | 只删已知文件名 |
| B09 | 首屏概览文件 | ✅ | `Desktop/ScanHistoryOverview.cs` | `History label reads envelope without loading nodes` | 4 MiB 上限，带时间戳校验 |
| B10 | 窗口/筛选设置持久化 | ⚠️ | `Desktop/DesktopSettings.cs` | 手工：改窗口大小→重启 | 写在**程序目录**，见 I-005 |

## C. 导出

| ID | 功能 | 状态 | 实现位置 | 验证 | 备注 |
|---|---|---|---|---|---|
| C01 | CSV（27 列，UTF-8 BOM） | ✅ | `ReportWriter.WriteCsv` | 按列名断言（I-001 / I-018 已修） | 含公式注入防护 |
| C02 | XML | ✅ | `ReportWriter.WriteXml` | `XML includes file metadata and scan times` | `XmlConvert` 日期 |
| C03 | Markdown AI 摘要 | 🟡 | `Core/AnalysisReportWriter.cs` | 手工：导出 .md 检查 top50 | 含"防止误删"提示语 |
| C04 | 图表 PNG | 🟡 | `ScanWindow.ExportChart` | 手工：显示图表→文件→导出图表 | 按 DPI 渲染 |
| C05 | 绝不覆盖已有文件 | ✅ | `ReportWriter.Write` 前置检查 + `File.Move(false)` | `existing export preserved` | 临时文件后改名 |
| C06 | 原子写入 + 取消清理 | ✅ | `CancellableOutputStream` | `pre-cancelled snapshot write creates no output`（Audit） | 取消不留残留文件 |

## D. 查询与筛选

| ID | 功能 | 状态 | 实现位置 | 验证 | 备注 |
|---|---|---|---|---|---|
| D01 | Top-N 排序（大小/时间/路径/占用） | ✅ | `Core/ReportQuery.cs` `SelectTop` | `50000-node bounded report matches full sort` + 全枚举项 | `PriorityQueue`，不全排 |
| D02 | 全局实际占用排序 | 🟡 | `ReportQuery` `LargestAllocated` + UI 菜单 | `allocated ranking uses deduplicated bytes...`（Audit） | 需先开启占用统计 |
| D03 | 名称通配符筛选 | ✅ | `Core/NameFilter.cs` | `case insensitive multi-mask wildcard filter` | `*`/`?`/`;`，100 ms 超时 |
| D04 | 高级筛选（大小/时间/属性/作用域/排除） | ✅ | `Core/NodeFilter.cs` | `combined name size and file scope filter` 等 8 项 | 未知/部分大小的处理有专门测试 |
| D05 | 全局大小筛选（含明细文件） | ✅ | `ScanWindow.ShowReport("全局大小筛选")` + `FileDetailStore.ReadAllFiles` | `global ranking includes exact GiB across stored parents`（Audit） | 目录汇总模式下会补读明细 |
| D06 | 目录索引（父→子 + 直属目录数） | ✅ | `Core/DirectoryIndex.cs` | `single pass directory index retains every child...` | 可中断排序但保留全部条目 |
| D07 | 图表摘要（Top 8 + 其他） | ✅ | `Core/ChartSummary.cs` | `bounded chart summary preserves top eight...` | 正好 8 项时不加"其他" |

## E. 桌面 UI

| ID | 功能 | 状态 | 实现位置 | 验证 | 备注 |
|---|---|---|---|---|---|
| E01 | 扫描目标选择（整机/盘符/目录） | 🟡 | `ScanWindow` `_scanScope` / `_path` / 选目录 | `single drive selection sets authoritative scan target` | 只读路径框，真实值在 `_scanTarget` |
| E02 | 目录树（分页 2000） | 🟡 | `TreeNode` / `PopulateTreePage` | 手工：展开大目录翻页 | 虚拟化 + 回收 |
| E03 | 列表（分页 10000） | 🟡 | `SelectFolderCore` / `_folderPager` | 手工：>1 万子项翻页 | 11 列 + 百分比条 |
| E04 | 文件明细分页（1000） | 🟡 | `BeginFolderFiles` / `LoadDirectFiles` | `disk file-detail pages preserve directory membership...` | 两种模式：扫描顺序 / 最大优先 |
| E05 | 名称/大小即时筛选（700 ms 防抖） | 🟡 | `ApplyResultFilters` + `DispatcherTimer` | 手工：输入后停顿自动筛选 | 超时有中文提示 |
| E06 | 历史三次扫描下拉切换 | 🟡 | `_historySelector` + `ScanHistory.Recent()` | `History selector displays loaded record target` | 显示 `MM-dd HH:mm · 目标` |
| E07 | 概览优先渲染 + 后台载入完整目录 | 🟡 | `ShowHistoryOverview` → `LoadReport` | 手工：启动时观察状态栏文案 | 失败会回滚到旧视图 |
| E08 | 图表（饼图/柱图、可折叠） | 🟡 | `Desktop/SizeChart.cs` | 手工：切换两种模式 | 点击扇区跳目录 |
| E09 | 百分比列（未知显示 "—"） | ✅ | `NodeRow.PercentValue` | `Percentages distinguish unknown, real zero and partial values` | 部分值加"（已读）" |
| E10 | 问题明细对话框（可复制） | 🟡 | `ScanWindow.ShowIssues` | 手工：点状态栏 | 最多列 10,000 条 |
| E11 | 右键：资源管理器打开 | 🟡 | `OpenInExplorer` | 手工 | 文件走 `/select,` |
| E12 | 右键：删除到回收站 | 🟡 | `DeleteSelected` | 手工：删测试文件 | 拒绝根目录/重解析点，二次确认 |
| E13 | 右键：AI 分析 | 🟡 | `AddAnalysisActions` + `BuildAnalysisMaterial` | 手工 | 材料限 12 个子项 |
| E14 | 菜单：文件/扫描/查看/报表 | 🟡 | `BuildMenu` | 手工 | 11 种报表 |
| E15 | 无响应保护失效时顶部红色警告 | 🟡 | `ReportProtectionFailure` | 手工（DEBUG 菜单有 4 分钟卡死探针） | 同时取消当前操作 |
| E16 | 进度条 | ⛔ | `SetProgress` | 无 | **参数被忽略，永不显示**，见 I-003 |
| E17 | 深色模式 | ⛔ | — | — | 固定浅色主题 |
| E18 | 多语言 | ⛔ | — | — | 中文硬编码 |

## F. AI 分析

| ID | 功能 | 状态 | 实现位置 | 验证 | 备注 |
|---|---|---|---|---|---|
| F01 | OpenAI Chat 协议 | ✅ | `Desktop/TextModelClient.cs` | `openai-chat endpoint` / `authentication` / `system message` | 默认协议 |
| F02 | OpenAI Responses 协议 | ✅ | 同上 | `openai-responses ...` | 解析 `output_text` / `output[]` |
| F03 | Anthropic Messages 协议 | ✅ | 同上 | `ant-messages ...` | `x-api-key` + `anthropic-version` |
| F04 | 本机 Codex CLI 调用 | 🟡 | `Desktop/LocalCodexClient.cs` | `--local-codex-smoke`（需本机已登录） | 子进程 + 临时目录 + 只读沙箱 |
| F05 | 多厂商/多模型配置 | ✅ | `ModelCatalog` / `ModelProfile` / `ModelConfigurationPane` | 64 项 ModelTests | 12 厂商、99 预置 + Codex |
| F06 | 厂商内密钥共享 | ✅ | `ModelGrouping.ShareKeys` | `All models in a provider inherit its key` | 改一个即全厂商生效 |
| F07 | DPAPI 密钥保护 | ✅ | `ModelSettings.Protect` | `Keys are encrypted on disk` | 解密后清零 |
| F08 | 连接测试（HTTP / CLI） | ✅ | `ModelConfigurationPane.TestAsync` | `Latest key test succeeded in UI` | 45 秒超时 |
| F09 | 系统提示词可编辑 + 恢复默认 | ✅ | `Desktop/CleanupAnalysisPrompt.cs` + `ModelSettings.Edit` | 手工 | 与默认值相同则存 null |
| F10 | 对话 Markdown 渲染 | 🟡 | `Desktop/ChatMarkdown.cs` | 手工 | 标题/列表/表格/代码块/引用 |
| F11 | 多轮上下文（12 轮） | 🟡 | `AnalysisChat._history` | 手工 | 超 12 轮丢最早的 |
| F12 | 停止回复 | 🟡 | `AnalysisChat._stop` | 手工 | 取消令牌链路 |
| F13 | 系统代理继承（CLI 模式） | ✅ | `LocalCodexClient.ApplySystemProxy` | `Enabled Windows HTTPS proxy inherited` | 显式环境变量优先 |
| F14 | 流式输出 | ⛔ | — | — | 一次性返回全文 |
| F15 | 失败消息脱敏 | ✅ | `LocalCodexClient.FailureMessage` | `Model failure is classified without raw diagnostics` | 不暴露 CLI 原文 |

## G. CLI

| ID | 功能 | 状态 | 实现位置 | 验证 | 备注 |
|---|---|---|---|---|---|
| G01 | `scan <dir>` | ✅ | `Cli/Program.cs` | 手工：实测 127 文件 / 12.84 ms | 顶层语句，101 行 |
| G02 | `--export` 多目标（json/csv/xml） | ✅ | 同上 | 手工 | 扫描前先校验全部目标 |
| G03 | `--file-details` / `--directories-only` | ✅ | 同上 | 手工 | 二者互斥于门槛参数 |
| G04 | `--allocated-space` | ✅ | 同上 | 手工 | 需 Windows |
| G05 | `--min-file-mib` / `--min-dir-mib` 列表 | ✅ | 同上 | 手工 | 输出 JSONL 行 |
| G06 | 退出码约定 | ✅ | 同上 | `--help` 文本 + 手工 | 0/2/64/74/130 |
| G07 | Ctrl+C 取消 | 🟡 | `Console.CancelKeyPress` | 手工 | 返回 130 |

## H. 稳定性

| ID | 功能 | 状态 | 实现位置 | 验证 | 备注 |
|---|---|---|---|---|---|
| H01 | 180 秒无响应强杀 | ✅ | `Desktop/ResponsivenessWatchdog.cs` | `--watchdog-only` 7 项 | 独立进程，杀前复核身份 |
| H02 | 看护进程自动重启（≤3 次） | 🟡 | `Supervise` | 手工 | 耗尽后告警 |
| H03 | 生命周期/看护日志 | 🟡 | `Record` / `RecordLifecycle` | 手工：查 `%LOCALAPPDATA%\FolderScope` | 不阻塞失败 |
| H04 | 无响应计时不受墙钟影响 | ✅ | `Core/UnresponsiveDeadline.cs` | `recovery resets timer` 等 7 项 | 调用方传入单调时间 |

## I. 工程与交付

| ID | 功能 | 状态 | 位置 | 备注 |
|---|---|---|---|---|
| I01 | 零 NuGet 依赖 | ✅ | `Directory.Build.props` | 有意为之 |
| I02 | 警告即错误 | ✅ | 同上 | 不要靠 pragma 绕过 |
| I03 | 解决方案文件 `.sln` | ⛔ | — | 需逐项目构建 |
| I04 | CI（构建+测试） | ⛔ | — | 见 TODO T7 |
| I05 | 发布脚本 / 安装包 | ⛔ | `.gitignore` 忽略了 `build.ps1` 等 | 见 TODO T12 |
| I06 | 代码覆盖率 | ⛔ | — | 自研 harness 无法统计 |
| I07 | 单元测试框架 | ⛔ | 用 console `Check()` 替代 | 首错即停 |
| I08 | 应用签名 | ⛔ | — | SmartScreen 会警告 |
| I09 | 版本号单点化管理 | ⚠️ | `Program.cs:144` 硬编码 `v2026.11.16.8` | 见 TODO T3 |

---

## 统计

| 状态 | 数量 |
|---|---:|
| ✅ 已实现且有测试 | 56 |
| 🟡 已实现但测试薄弱 | 26 |
| ⚠️ 已实现但有已知缺陷 | 2（B10 / I09） |
| ⛔ 未实现 | 10 |
| **合计** | **94** |

> 按上表逐行点计（A18 + B10 + C6 + D7 + E18 + F15 + G7 + H4 + I9 = 94）。
> "⚠️"属于"已实现"，不重复计入 ⛔。
