# 测试说明

## 1. 三套测试入口

项目**不使用 xUnit/NUnit**，全部是可直接运行的 console 程序，靠 `Check(bool, string label)` 断言。

| 入口 | 项目 | 目标框架 | 依赖 | 作用 |
|---|---|---|---|---|
| 核心回归 | `tests/FolderScope.Tests` | `net10.0` | 无 | 扫描、快照、明细、查询、筛选、取消、限额 |
| 模型与 UI | `tests/FolderScope.ModelTests` | `net10.0-windows` | WPF | 模型配置、协议请求、DPAPI、WPF 交互 |
| 审计/补充 | `tests/FolderScope.Tests --audit-only` | `net10.0` | 文件系统 | 合盘、落盘、索引回退、离线文件、快照预检 |

### 1.1 核心回归的四种模式

```powershell
# 默认：完整回归（可选参数指定产物目录，默认在 bin 下 test-artifacts）
dotnet run --project tests/FolderScope.Tests/FolderScope.Tests.csproj -c Release -- "$env:TEMP\fs-core"

# 只看护计时（7 项，纯内存，不碰磁盘）
dotnet run --project tests/FolderScope.Tests/FolderScope.Tests.csproj -c Release -- --watchdog-only

# 明细存储故障注入（约 12 项，模拟索引损坏/磁盘写失败）
dotnet run --project tests/FolderScope.Tests/FolderScope.Tests.csproj -c Release -- --store-fault-only

# 审计用例（39 个 Check，任意检出目录可运行；项目根自动定位或由 FLASHSCOPE_AUDIT_ROOT 指定）
dotnet run --project tests/FolderScope.Tests/FolderScope.Tests.csproj -c Release -- --audit-only
```

### 1.2 模型测试的三种模式

```powershell
# 默认：64 项，全部使用模拟 HTTP，不联网
dotnet run --project tests/FolderScope.ModelTests/FolderScope.ModelTests.csproj -c Release

# 真实调用本机 Codex CLI（需要已 codex login）
... -- --local-codex-smoke

# 额外把主窗口渲染成 PNG 到 research/ 下（用于人工看布局）
... -- --layout-preview
```

环境变量 `FOLDERSCOPE_SPARSE_PROBE=<稀疏文件路径>` 可让核心测试额外验证稀疏文件占用（可选）。

---

## 2. 当前结果（2026-11-20，基线 `da32d6b`）

| 入口 | 结果 | 说明 |
|---|---|---|
| 构建（4 项目 Release） | ✅ 0 warning / 0 error | `TreatWarningsAsErrors` 生效 |
| `ModelTests` | ✅ `PASS 64 model configuration checks. Mock requests only.` | 退出码 0 |
| `Tests --watchdog-only` | ✅ `Watchdog timing checks: 7` | 退出码 0 |
| `Tests --store-fault-only` | ✅ 12 项 PASS | 退出码 0 |
| `Tests`（默认） | ✅ `Completed 134 checks` | 退出码 0；修复前在第 68 项失败（I-001 / I-018） |
| `Tests --audit-only` | ✅ `Audit checks: 39 passed, 0 failed` | 退出码 0；修复前因硬编码路径不可运行（I-002） |

> 核心测试的 116 个 `Check(...)` 调用点中，部分位于循环内，实际执行 **134** 次。
> 全部入口目前为绿；任何新增失败都应先对照 `docs/ISSUES.md` 判断是回归还是新增。

---

## 3. 覆盖矩阵

| 被测能力 | 测试入口 | 代表用例（`Check` 标签） |
|---|---|---|
| 扫描总量与层级 | 核心 | `exact bytes/counts including empty directory`、`child aggregation` |
| 取消与部分结果 | 核心 | `cancellation reports partial state`、`last periodic progress cancellation preserves partial state` |
| 节点/内存限额 | 核心 | `node safety limit returns marked partial result`、`managed memory budget returns marked partial result` |
| 目录汇总模式 | 核心 | `directory-only retention preserves full totals...`、`directory retention at exact node cap...` |
| 实际占用与硬链接 | 核心 | `allocated totals agree across full and directory scans...`、`unavailable hard-link identity never invents deduplication` |
| 明细落盘/分页/排序 | 核心 | `scan stores all file details on disk...`、`disk file-detail largest query is bounded...` |
| 索引损坏回退 | 核心 + 审计 | `malformed disk-detail index falls back...`、`corrupted and legacy indexes fall back...` |
| 快照往返与校验 | 核心 | `saved scan loads with exact hierarchy and sizes`、`corrupt snapshot aggregate rejected` |
| 快照预检/体积上限 | 核心 + 审计 | `snapshot node cap rejects before object deserialization`、`snapshot preflight chunk boundaries...` |
| Top-N 与排序 | 核心 | `50000-node bounded report matches full sort`、全 `ReportOrder` 枚举 |
| 筛选 | 核心 + 审计 | `combined name size and file scope filter`、`size bounds distinguish zero unknown and partial totals` |
| 图表摘要 | 核心 | `bounded chart summary preserves top eight...` |
| 看护计时 | 核心 | `--watchdog-only` 7 项 |
| 扫描告警（合盘/离线/写失败） | 审计 | `multi-root merge preserves totals...`、`both allocation readers reject offline files` |
| AI 协议与配置 | 模型 | 3 种协议 × 端点/鉴权/系统消息/正文解析 |
| 密钥保护 | 模型 | `Keys are encrypted on disk`、`Unlock` 往返 |
| WPF 交互 | 模型 | `Latest key test succeeded in UI`、`Main selection persists...` |

### 明确**没有**测试覆盖的部分

- `ScanWindow` 的扫描流程、分页、右键菜单、删除到回收站（仅 `--layout-preview` 人工看）
- 图表绘制（`SizeChart.OnRender`）
- `ScanHistory` 的三条淘汰逻辑
- `ChatMarkdown` 渲染细节
- 看护进程真实强杀（只有计时单元测试）
- CLI 各参数组合（仅手工冒烟）
- 所有网络相关（模型测试全部用 `FakeHandler`）

---

## 4. 如何新增一个测试

### 核心回归

```csharp
// tests/FolderScope.Tests/Program.cs，放在最后一个 Check 之后
Check(condition, "behavior in plain english");
```

约定：

1. **一个标签描述一种行为**，不要写实现细节。
2. 自建临时目录，放在 `root`（每次运行唯一）下。
3. 结束只删除自己创建的确切文件，不要 `Directory.Delete(root)` 之外的递归删除。
4. 涉及权限/时间等环境因素时用 `try/catch` 输出 `NOT TESTED`，不要让整轮崩掉。
5. 不要依赖开发机路径。

### 模型测试

```csharp
using var handler = new FakeHandler(request => Reply("openai-chat"));
using var http = new HttpClient(handler);
var answer = TextModelClient.SendAsync(profile, "system", turns, default, http).GetAwaiter().GetResult();
Check(answer == "OK", "visible text");
```

不要在这里发真实网络请求；真实调用只在 `--local-codex-smoke` 分支。

### 审计用例

在 `AuditChecks.Run()` 的 `Check("名称", () => { ... })` 里加；失败会被收集而不是中断。注意 `scratch` 只能放在 `research/` 下（I-002 修复后改为参数化）。

---

## 5. 测试隔离与副作用

| 项 | 行为 |
|---|---|
| 临时目录 | 每次运行 `Guid.NewGuid()`，互不干扰 |
| 模型配置 | 写入唯一临时文件，不触碰 `%LOCALAPPDATA%\FolderScope` |
| 注册表/代理 | 只构造 `ProcessStartInfo` 对象，不实际改环境 |
| 网络 | 全部 mock |
| 符号链接 | 创建失败时输出 `NOT TESTED`，不判失败 |
| 产物清理 | 核心测试保留产物目录并打印路径（便于排查）；模型测试删除自己创建的文件 |
| 审计测试 | `finally` 中只删 `research/audit-tiny-*` 且非重解析点 |

---

## 6. 已知测试工程问题

| 问题 | 影响 | 跟踪 |
|---|---|---|
| 首错即停，不汇总 | 一次只能看到一个问题 | I-013 / T12 |
| ~~`--audit-only` 不可移植~~ | 已解决：自动定位项目根 + `FLASHSCOPE_AUDIT_ROOT` 覆盖 | ✅ I-002 / T2 |
| 无覆盖率统计 | 不知道哪些分支没测 | TODO 未排期 |
| 无可并行 | 全套串行，约数十秒 | 可接受 |
| WPF 测试依赖 `Dispatcher` | 只能 `[STAThread]` 单线程跑 | 设计限制 |
