# 数据格式契约

> 改动本文件描述的任何格式前，先读 `ReportReader` 的校验逻辑——它对快照做了严格的全量校验，改字段必须同时加迁移。
>
> 所有 JSON 均为 UTF-8；时间字段为 ISO-8601（序列化用 `"O"`，即 UTC 往返格式）。

---

## 1. 扫描快照（`.json`）

**Schema 标识**：`"Schema": "folderscope.scan.v1"`（`ScanReport.Schema`，只读常量）。
`SnapshotPreflight` 会拒绝任何其他值，包括上游旧的 `.fsp` 格式。

### 1.1 顶层字段（实测导出的完整键集）

| 键 | 类型 | 含义 | 读取时校验 |
|---|---|---|---|
| `Schema` | string | 固定 `folderscope.scan.v1` | 必须存在且相等 |
| `RootPath` | string | 扫描目标；整机扫描为 `"整个电脑"` | 非空 |
| `StartedUtc` / `FinishedUtc` | DateTime | 起止时间（UTC） | — |
| `ByteSemantics` | string | 大小口径的自述文本（只读计算属性） | 忽略 |
| `DuplicateHardLinkPaths` | long | 被去重掉的硬链接路径数 | ≥ 0 |
| `MeasuresAllocatedSpace` | bool | 是否统计了实际占用 | — |
| `AllocationTotalsVersion` | int | 占用聚合算法版本（有占用时为 1） | — |
| `AllocationSemantics` | string | 占用口径自述（只读计算属性） | 忽略 |
| `Cancelled` | bool | 是否被取消 | — |
| `RetainsFileNodes` | bool | 是否保留文件节点；**总是写出**（缺失的旧快照按 true 处理） | — |
| `FileDetailsPath` | string? | 外部明细文件路径 | 有 `StoreId` 时不能为空 |
| `FileDetailsStoreId` | Guid? | 明细文件身份 | 不能是 `Guid.Empty` |
| `MinimumFileBytes` | long | 快速估算门槛（0 = 全量） | ≥ 0 |
| `EstimateExcludedFiles` | long | 被门槛排除的文件数 | — |
| `UnlistedSkippedLinks` | long | 目录汇总模式下跳过的链接数 | ≥ 0 |
| `Nodes` | ScanNode[] | **节点表，id 即下标** | 非空、根节点合法、父子无环 |
| `Issues` | ScanIssue[] | 问题明细（≤ 10,000） | ≤ 10,000 |
| `SuppressedIssueCount` | long | 被省略的问题数 | ≥ 0 |
| `Complete` | bool | 只读计算属性，**写入但读取时忽略** | — |
| `Root` | ScanNode | 只读计算属性 `Nodes[0]`，**写入但读取时忽略** | — |

> ⚠️ 快照里 `Root` 是 `Nodes[0]` 的一份完整副本，`Complete` 是派生值。它们只为方便外部工具阅读而存在；**修改 `Nodes` 后不要忘了二者会不一致**——程序自身只信 `Nodes`。

### 1.2 `ScanNode`

| 键 | 类型 | 含义 |
|---|---|---|
| `Id` | int | 节点编号，恒等于数组下标 |
| `ParentId` | int? | 父节点 id；根为 null；恒小于 `Id` |
| `Path` | string | 完整路径（内部可能由"父路径 + Name"按需拼出） |
| `Name` | string | 文件/目录名 |
| `IsDirectory` | bool | 是否目录 |
| `OwnBytes` | long | 自身逻辑字节（目录恒 0） |
| `TotalBytes` | long | 累计逻辑字节（含全部子孙） |
| `TotalFiles` | long | 累计文件数 |
| `TotalDirectories` | long | 累计子目录数（不含自身） |
| `UnlistedFileBytes` / `UnlistedFileCount` | long | 目录汇总模式下未单列的文件合计 |
| `UnlistedAllocatedBytes` | long? | 目录汇总模式下未单列文件的占用 |
| `FileAllocatedBytes` | long? | 本文件的数据块占用（硬链接去重前） |
| `OwnAllocatedBytes` / `TotalAllocatedBytes` | long? | 去重后占用；未知为 null |
| `HardLinkDuplicate` | bool | 本路径是重复硬链接（占用已计入别处） |
| `Attributes` | int | `FileAttributes` 原值 |
| `ModifiedUtc` / `CreatedUtc` / `AccessedUtc` | DateTime? | 文件系统时间 |
| `LinkSkipped` | bool | 重解析点被跳过 |
| `Incomplete` | bool | 未完整读取 |
| `IncompleteReasons` | int（Flags） | 见下 |

`IncompleteReasons` 位标志：

| 值 | 名称 | 含义 |
|---:|---|---|
| 0 | None | — |
| 1 | AccessDenied | 拒绝访问（Win32 错误码 5） |
| 2 | ReadError | 其他读取失败 |
| 4 | LinkSkipped | 跳过链接/重解析点 |
| 8 | Cancelled | 用户取消 |
| 16 | NodeLimit | 达到节点上限 |
| 32 | MemoryLimit | 达到内存上限 |
| 64 | DetailStorage | 明细写入失败 |

> `PathLength` 不序列化（`[JsonIgnore]`），运行时按需计算。

### 1.3 `ScanIssue`

```json
{ "Path": "D:\\x", "Operation": "enumerate", "Message": "...", "ErrorCode": 5 }
```

`Operation` 取值：`enumerate`、`metadata`、`allocated-space`、`drive-enumeration`、`drive-unavailable`、`node-limit`、`memory-limit`、`scan-cache-excluded`、`file-detail-write`、`file-detail-flush`、`file-detail-index`。

### 1.4 两种写出模式

| 方法 | 序列化选项 | 用途 |
|---|---|---|
| `ReportWriter.Write` | `WriteIndented = true`，保留全部字段 | 用户导出、CLI `--export` |
| `ReportWriter.WriteHistorySnapshot` | `DefaultIgnoreCondition = WhenWritingDefault` | 自动历史快照，省略 0/false/null 以省空间 |

例外：`RetainsFileNodes` 强制总是写出（`JsonIgnoreCondition.Never`），因为"缺失"在旧快照里表示 `true`。

---

## 2. CSV 导出（27 列）

UTF-8 **带 BOM**，所有单元格加双引号，内部 `"` 转义为 `""`。
以 `= + - @ \t \r \n` 开头的值前置 `'`，防止 Excel 公式注入。

| # | 列名 | 来源 |
|---:|---|---|
| 1 | `Id` | `node.Id` |
| 2 | `ParentId` | `node.ParentId`（根为空） |
| 3 | `Path` | `node.Path` |
| 4 | `Kind` | `Directory` / `File` |
| 5 | `OwnBytes` | `node.OwnBytes` |
| 6 | `TotalBytes` | `node.TotalBytes` |
| 7 | `TotalFiles` | `node.TotalFiles` |
| 8 | `TotalDirectories` | `node.TotalDirectories` |
| 9 | `ModifiedUtc` | `"O"` 格式或空 |
| 10 | `LinkSkipped` | bool |
| 11 | `Incomplete` | bool |
| 12 | `ScanCancelled` | 整份扫描是否取消（每行相同） |
| 13 | `Name` | `node.Name` |
| 14 | `CreatedUtc` | `"O"` 或空 |
| 15 | `AccessedUtc` | `"O"` 或空 |
| 16 | `Attributes` | int |
| 17 | `DirectDirectories` | 直属子目录数（与 `TotalDirectories` 不同） |
| 18 | `PathLength` | 字符数 |
| 19 | `NameLength` | 字符数 |
| 20 | `ScanComplete` | 整份扫描是否完整（每行相同） |
| 21 | `SuppressedIssueCount` | 整份扫描 |
| 22 | `RetainsFileNodes` | 整份扫描 |
| 23 | `UnlistedFileBytes` | 目录汇总模式 |
| 24 | `UnlistedFileCount` | 目录汇总模式 |
| 25 | `SizeState` | `Known` / `Partial` / `Unknown` / `SkippedLink` |
| 26 | `KnownLogicalBytes` | 可排序的字节数（未知为空） |
| 27 | `IsLowerBound` | `SizeState == Partial` |

> **兼容注意**：新增列只能追加到末尾。测试通过 `CsvCells` 按列名定位，列顺序变化不会误判；但修改列名会让测试以明确标签失败，需同步更新本表。

---

## 3. XML 导出

```xml
<scan schema="folderscope.scan.v1" retainsFileNodes="true" unlistedSkippedLinks="0"
      root="D:\data" startedUtc="..." finishedUtc="..." complete="true" cancelled="false"
      suppressedIssueCount="0" byteSemantics="...">
  <node id="0" path="D:\data" name="data" directory="true" ownBytes="0"
        unlistedFileBytes="0" unlistedFileCount="0" bytes="123" sizeState="Known"
        knownLogicalBytes="123" isLowerBound="false" files="5" directories="2"
        linkSkipped="false" incomplete="false" attributes="16"
        modifiedUtc="..." createdUtc="..." accessedUtc="..." />
  <issue path="..." operation="...">message</issue>
</scan>
```

可选属性（`knownLogicalBytes`、时间、`parentId`）在无值时省略。

---

## 4. 文件明细（`.jsonl`）

**append-only**，一行一条记录，首行是信封：

```json
{"DetailSchema":"folderscope.details.v1","StoreId":"3f2c...-guid"}
```

后续每行是一个 `ScanNode`（必为文件，`ParentId` 非空）。字段与快照中的 `ScanNode` 相同。
写入用 `FileMode.CreateNew`，**永不覆盖**；同一路径只能创建一次。

### 4.1 索引文件（`<details>.jsonl.index.json`）

```json
{
  "Length": 123456,
  "LastWriteTicks": 638...,
  "Ranges": { "0": [ { "Start": 81, "End": 240 } ], "7": [ ... ] },
  "Version": 1,
  "Digest": "SHA256-HEX-64-CHARS"
}
```

- `Ranges`：`parentId → 该目录下文件记录的字节区间列表`。
- `Digest`：对 `Version/Length/LastWriteTicks/区间表` 计算的 SHA-256（防误用，**不是**身份认证）。
- 读取时以下任一不满足即**回退到顺序全量读取**：文件存在、≤ 8 MiB、`Length` 与文件实际长度一致、`LastWriteTicks` 一致、`Version == 1`、摘要匹配、区间恰好无缝覆盖数据区。

### 4.2 记录校验（`ValidateFileRecord`）

单条记录必须满足：非目录、`ParentId ≥ 0`、路径/名称非空、字节非负、`TotalBytes == OwnBytes`、`TotalFiles ∈ {0,1}`、链接跳过项的大小必须为 0 且占用为 null、`HardLinkDuplicate` 项的 `OwnAllocatedBytes == 0` 且 `FileAllocatedBytes` 非空。任何一条不满足 → `InvalidDataException`。

---

## 5. 首屏概览（`overview-v1.json`）

与快照同目录，文件名固定 `overview-v1.json`，**≤ 4 MiB**。

| 字段 | 含义 |
|---|---|
| `SnapshotLength` / `SnapshotWriteUtc` | 生成时快照的长度与写入时间，用于**失效检测** |
| `RootPath` / `FinishedUtc` / `MinimumFileBytes` / `Complete` / `MeasuresAllocatedSpace` | 快照元信息副本 |
| `Root` | 根节点 |
| `Entries` | 根的直接子项（≤ 100），元素为 `{ Node, DirectDirectories }` |
| `HasMore` | 根的子项是否超过 100 |
| `DrivePages` | 整机扫描时：每个盘符的前 256 项 |
| `FolderPages` | 整机扫描时：每个盘符下前 8 个目录，各前 24 项 |

**失效规则**：读取时若 `(快照长度, 快照写入时间)` 与当前文件不一致，整个概览作废，回落到完整快照加载。概览永远只是"预览"，权威数据是 `snapshot.json`。

---

## 6. 模型设置（`%LOCALAPPDATA%\FolderScope\model-settings.json`）

```json
{
  "BaseUrl": "https://api.deepseek.com",
  "Model": "deepseek-flash",
  "ProtectedKey": "base64-DPAPI-blob",
  "AnalysisPrompt": null,
  "Provider": "deepseek",
  "ClientType": "openai-chat",
  "DefaultProfileKey": "deepseek:deepseek-v4-flash",
  "ModelProfiles": [
    {
      "Key": "deepseek:deepseek-v4-flash",
      "ProviderId": "deepseek",
      "ProviderName": "DeepSeek",
      "DisplayName": "DeepSeek V4 Flash",
      "BaseUrl": "https://api.deepseek.com/v1",
      "Model": "deepseek-v4-flash",
      "ClientType": "openai-chat",
      "ProtectedKey": "base64-DPAPI-blob",
      "IsCustom": false,
      "CodexCommand": "codex"
    }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `ProtectedKey` | **DPAPI（当前用户）加密后 Base64**。同一厂商下所有模型共享同一密钥。空字符串 = 未配置 |
| `AnalysisPrompt` | `null` 表示使用内置默认提示词；与默认值完全相同也会存 `null` |
| `ClientType` | `openai-chat` / `openai-responses` / `ant-messages` / `codex-cli` |
| `DefaultProfileKey` | 当前默认模型；`ModelProfiles` 中必须存在 |
| `CodexCommand` | 仅 `codex-cli` 使用；可填完整路径 |
| 未知字段 | 顶层与 `ModelProfile` 都有 `[JsonExtensionData]`，**原样保留**，用于向前兼容 |

预设目录来自嵌入资源 `FolderScope.ModelPresets.json`（12 厂商、99 个模型 + 1 个 Codex CLI 条目）。用户配置按键合并覆盖预设，删除用户配置项即可回落到预设。

---

## 7. 桌面设置（`folderscope.settings.json`）

> ⚠️ 位置是**程序目录**（`AppContext.BaseDirectory`），不是 `%LOCALAPPDATA%`。见 I-005。

| 字段 | 默认 | 说明 |
|---|---|---|
| `Width` / `Height` | 1120 / 730 | 窗口尺寸，加载时夹在 800–3840 / 530–2160 |
| `LastPath` | `""` | 上次扫描目标 |
| `NameMasks` | `""` | 名称通配符规则（`;` 分隔） |
| `Exclude` | false | 名称规则是否取反 |
| `Bars` | false | 图表用柱状图 |
| `ShowChart` | false | 是否显示图表 |
| `MemoryBudgetGiB` | 8 | 只允许 2/4/8/16，否则视为无效 |
| `RetainFileNodes` | true | 是否保留文件节点 |
| `AdvancedFilter` | null | `NodeFilterOptions`（名称/大小/时间/属性/作用域/排除） |

无效值（`NaN` 尺寸、非法内存预算）会抛 `InvalidDataException`，UI 捕获后显示"设置未恢复"，不影响启动。

---

## 8. 兼容规则

1. **快照只增不改**：新字段必须可缺省，且 `ReportReader` 的校验不能因缺失而失败。
2. **`RetainsFileNodes` 缺失 = true**：旧快照默认保留文件节点，写出时必须显式写 `false`。
3. **CSV 只追加列**，且必须同步更新测试的列索引。
4. **明细信封版本**：`folderscope.details.v1`。改结构需升 `v2` 并保留 v1 读取分支。
5. **旧明细无信封**：文件首行不是合法信封时按"纯记录文件"顺序读取（`ReadHeader` 回退）。
6. **设置文件向前兼容**：`[JsonExtensionData]` 必须保留未知键，避免旧版本覆盖新版本配置。
