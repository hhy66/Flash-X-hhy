# 开发进度表

> 基线提交：`da32d6b`（`Show measured Flash-X scan in README`）
> 接手日期：2026-11-20
> 状态图例：✅ 完成 ｜ 🔄 进行中 ｜ ⏸ 暂缓 ｜ ⛔ 未开始

---

## 1. 里程碑总览

| 里程碑 | 目标 | 状态 | 完成度 | 依据 |
|---|---|---|---|---|
| **M0 接手基线** | 读懂全部代码、建立文档体系、确认构建与测试真实状态 | ✅ | 100% | 本文第 3、4 节 |
| **M1 修复红灯** | 让主测试套件全绿、`--audit-only` 可移植 | ✅ | 100% | 134 + 39 项全过，见第 4 节 |
| **M2 工程化** | CI、`.sln`、版本号单点化、设置写标准位置 | ⛔ | 0% | `TODO.md` T3–T8 |
| **M3 可维护性** | 拆分 1,602 行的 `Program.cs`、改进测试 harness | ⛔ | 0% | `TODO.md` T6 / T13 |
| **M4 产品增强** | 流式 AI、多语言、安装包与签名 | ⛔ | 0% | `TODO.md` T9–T14 |

---

## 2. 模块完成度

按 `FEATURES.md` 逐项统计（"⚠️"计入已实现，但不计入健康）。

| 模块 | 已实现 / 总计 | 健康度 | 主要缺口 |
|---|---:|---|---|
| A 扫描引擎 | 18 / 18 | 高 | 实时预览仅手工验证 |
| B 持久化与快照 | 10 / 10 | 高 | 设置写在程序目录（I-005） |
| C 导出 | 6 / 6 | 中 | CSV 断言失效（I-001） |
| D 查询与筛选 | 7 / 7 | 高 | — |
| E 桌面 UI | 15 / 18 | 中 | 进度条失效（I-003）、无深色模式、无多语言 |
| F AI 分析 | 14 / 15 | 中 | 无流式输出；CLI 模式依赖本机登录 |
| G CLI | 7 / 7 | 中 | 取消路径仅手工验证 |
| H 稳定性 | 4 / 4 | 高 | 看护进程重启仅手工验证 |
| I 工程与交付 | 3 / 9 | 低 | 无 CI / 无 `.sln` / 无覆盖率 / 无安装包 |

**结论**：核心引擎（扫描 + 快照）质量最高，大量不变量有测试锁定；短板集中在**交付工程**与**UI 细节**，不在算法。

---

## 3. 本阶段（M0）工作记录

| # | 工作项 | 状态 | 产出 | 日期 |
|---|---|---|---|---|
| 1 | 全量通读源码（46 个 `.cs`，约 7,100 行；含 `.csproj`、`props`、`ModelPresets.json`、`README`、`.gitignore`） | ✅ | 架构理解见 `ARCHITECTURE.md` | 2026-11-20 |
| 2 | 通读 `research/install-notes.md`（本地笔记，已被 `.gitignore` 排除） | ✅ | 内容已吸收进 `DEVELOPMENT.md` | 2026-11-20 |
| 3 | Release 构建四个项目 | ✅ | 全部 0 warning / 0 error | 2026-11-20 |
| 4 | 运行模型测试 | ✅ | `PASS 63 model configuration checks` | 2026-11-20 |
| 5 | 运行核心测试 | ⚠️ | 67 项通过后在第 68 项（CSV 断言）失败退出 | 2026-11-20 |
| 6 | 运行看护计时测试 | ✅ | `Watchdog timing checks: 7` | 2026-11-20 |
| 7 | 运行 `--audit-only` | ⛔ | 硬编码路径抛异常 | 2026-11-20 |
| 8 | 验证 CLI `--help` | ✅ | 退出码 0，文案完整 | 2026-11-20 |
| 9 | 建立文档体系（11 份） | ✅ | `docs/` + 根 `CLAUDE.md` | 2026-11-20 |
| 10 | 修复 I-001：3 处 CSV 断言改为按列名定位，新增 RFC4180 解析函数 | ✅ | `Program.cs` 的 `CsvCells` | 2026-11-20 |
| 11 | 修复 I-018：2 处异常文案断言对齐实现 | ✅ | 第 91 项不再失败 | 2026-11-20 |
| 12 | 修复 I-002：审计项目根参数化 + 围栏加固 | ✅ | `AuditChecks.ResolveProjectRoot` | 2026-11-20 |
| 13 | 全量复跑 5 个测试入口 | ✅ | 134 / 39 / 12 / 7 / 63 全过 | 2026-11-20 |
| 14 | 修复 I-017：扫描链路 4 处补齐 `InvalidDataException` / `JsonException` 捕获 | ✅ | 与 `LoadReport` 对齐 | 2026-11-20 |
| 15 | 修复 I-019：启动期设置损坏不再崩溃 | ✅ | 构造函数降级为状态提示 | 2026-11-20 |
| 16 | 修复 I-015：剪贴板占用不再抛未处理异常 | ✅ | `AnalysisChat` 状态行提示 | 2026-11-20 |
| 17 | 新增设置损坏回归测试 | ✅ | 模型测试 63 → 64 | 2026-11-20 |

---

## 4. 验证证据（可复跑）

环境：Windows 11 Pro for Workstations，.NET SDK `10.0.404`，Release 配置。

```powershell
# 1) 构建：四个项目全部成功，0 warning / 0 error
dotnet build src/FolderScope.Desktop/FolderScope.Desktop.csproj -c Release
dotnet build src/FolderScope.Cli/FolderScope.Cli.csproj        -c Release
dotnet build tests/FolderScope.ModelTests/FolderScope.ModelTests.csproj -c Release
dotnet build tests/FolderScope.Tests/FolderScope.Tests.csproj -c Release
```

| # | 命令 | 修复前 | 现在 |
|---|---|---|---|
| 1 | `ModelTests`（默认模式） | ✅ 63 | ✅ `PASS 64 model configuration checks`，退出码 0 |
| 2 | `Tests --watchdog-only` | ✅ 7 | ✅ `Watchdog timing checks: 7`，退出码 0 |
| 3 | `Tests --store-fault-only` | ✅ 12 | ✅ 12 项通过，退出码 0 |
| 4 | `Cli --help` | ✅ | ✅ 退出码 0 |
| 5 | `Tests <临时目录>` | ❌ 第 68 项失败（I-001） | ✅ `Completed 134 checks`，退出码 0 |
| 6 | `Tests --audit-only` | ❌ 硬编码路径（I-002） | ✅ `Audit checks: 39 passed, 0 failed`，退出码 0 |

> 核心测试有 116 个 `Check(...)` 调用点，部分位于循环内，实际执行 **134** 次。
> 修复前"首错即停"使第 68 项之后的用例全部未执行；本次修复 I-001 后，又暴露出 I-018 两处同类断言，一并处理后才达成全绿。
>
> 计数命令：`(Select-String -Path <日志> -Pattern '^PASS ').Count` → `134`。

---

## 5. 风险与阻碍

| 风险 | 影响 | 缓解 |
|---|---|---|
| ~~主测试套件红灯~~ | 已解决 | 134 项全绿（2026-11-20，I-001 + I-018） |
| ~~`--audit-only` 不可移植~~ | 已解决 | 39 项可在任意检出目录运行（I-002） |
| 无 CI | 红灯可能长期无人发现 | M2 增加 GitHub Actions |
| `Program.cs` 1,602 行 | 改动冲突概率高、审查困难 | M3 按职责拆分（partial class 或独立控件类） |
| 自研测试 harness 首错即停 | 一次只能看到一个问题 | M3 改为汇总所有失败后统一退出 |
| 仅 Windows 可构建/运行 | 无法在 Linux CI 直接跑 WPF | CI 用 `windows-latest`；Core 测试可跨平台部分运行 |
| `research/` 被 gitignore | 接手笔记/测量数据丢失 | 已迁入 `docs/`，后续数据放 `docs/measurements/`（待建） |

---

## 6. M1 完成记录

| 顺序 | 任务 | 结果 |
|---|---|---|
| 1 | 修 `Program.cs:404` CSV 断言（按列名索引） | ✅ 同时修复 `502`、`572`，新增 `CsvCells` 解析 |
| 2 | 修 `AuditChecks.cs:19` 硬编码路径 | ✅ `ResolveProjectRoot()` + 环境变量 + 围栏加固 |
| 3 | 修同类异常文案断言（I-018） | ✅ 节点上限与体积上限两项对齐实现 |
| 4 | 复跑全部入口 | ✅ 134 / 39 / 12 / 7 / 64 全过 |

## 7. 下一阶段（M2 工程化）

| 顺序 | 任务 | 预估 | 关联 |
|---|---|---|---|
| 1 | 增加 GitHub Actions（Windows，构建 + 测试） | 2 h | T7 / I-011 |
| 2 | `DesktopSettings` 迁移到 `%LOCALAPPDATA%` | 1.5 h | T9 / I-005 |
| 3 | 版本号单点化 | 0.5 h | T8 / I-004 |
| 4 | 进度条接真实进度或删除死代码 | 1 h | T10 / I-003 |

详见 `TODO.md` 的 P1 / P2 区块。
