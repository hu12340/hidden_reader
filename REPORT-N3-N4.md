# N3–N4 复审修复交付报告

分支：`feature/first-release`（开工 HEAD = `c836a06`「修复」，与比较基线一致，工作区干净）
日期：2026-11-06　环境：Windows 11、.NET 10；本轮全部用隔离书库直接验证正式 `Library` 实现

## 1. 修改位置

仅 `src/HiddenReader.App/Library/Library.cs`（`git status` 唯一代码改动）。

### N3｜保留清单不可信时跳过启动清理

- 原 `LoadPreservedList` 把「文件不存在」「读取失败」「JSON 损坏」一律吞掉并返回空集合——调用方会把失败结果当空清单写回，覆盖故障现场；清理也可能误删受保护副本。
- 新增 `TryLoadPreservedList(out names)` 三态判定（`PreservedListStatus.Missing / Valid / Invalid`）：
  - 用 `File.OpenRead` + `FileNotFoundException / DirectoryNotFoundException` 判定「确实不存在」——`File.Exists` 在权限不足时同样返回 false，会把 I/O 错误误判为不存在；权限、被占用等其他异常判为 `Invalid` 并记录原因；
  - 用 `JsonDocument.Parse` 校验：根必须是数组、元素必须是字符串；内容 `null`、结构错误、JSON 损坏均判 `Invalid`（记录原因）；
  - `Valid` 包含显式 `[]`（有效空清单）。
- 两个读取调用方按状态分流：
  - `PreserveExistingCopies`：`Invalid` 时跳过并记日志 `preserve skipped: preserved list unusable, fault site kept`，**不写回**；`Missing/Valid` 时照常新增并写回。
  - `CleanupOrphans`：`Invalid` 时记日志 `startup cleanup skipped: preserved list unusable` 并**跳过整次清理**（无法区分受保护副本与孤儿，宁可少删不可误删）；`Missing/Valid` 照常按白名单清理。写回裁剪清单只发生在可信读取之后。
- 同步更新 `CleanupOrphans` XML 注释。

### N4｜拒绝缺少 Books 字段的书架状态

- `Library.Load` 改为先 `JsonDocument.Parse` 校验：根为对象、显式含 `Books` 属性且 `ValueKind == Array` 才认作有效书架，否则抛错走现有无效状态保护路径（保留原 `library.json`、`PreserveExistingCopies` 记录副本、禁止清理与空书架覆盖）。
- 原因：`AppState.Books` 有属性初始化器（`= new()`），字段缺失时反序列化结果是空列表而非 null，仅检查反序列化结果会把不完整/被截断的状态误当空书架进而清理副本。显式 `"Books": []` 仍是有效空书架（`ValueKind.Array`）。

## 2. 实际命令与退出码

```
dotnet build src/HiddenReader.App/HiddenReader.App.csproj -v q
    # 退出码 0
dotnet build src/HiddenReader.App/HiddenReader.App.csproj --no-incremental
    # 退出码 0；2 条警告均为基线已有 CS8600（MainWindow.xaml.cs，本轮未触碰该文件）
dotnet run --project tools/Probe/Probe.csproj -c Debug   # 探针，退出码 0，跑完已删
隔离目录：HR_DATA_DIR=%TEMP%\hr-n34-probe\<场景>；HR_LOG_DIR=%TEMP%\hr-n34-probe\logs（探针全程共用，见 §4 注意）
清理：探针目录、tools/Probe 均已删除；无应用实例启动（直接验证 Library），无残留进程
```

## 3. 复验结果（探针 16/16 通过，证据为探针输出与 %TEMP% 快照哈希对比）

### N3（10 项）

| 用例 | 结果 |
| --- | --- |
| 缺失 library.json 启动 → 旧副本记入保留清单（N1 行为保持） | ✅ |
| 保留清单损坏 + 状态仍缺失 → `PreserveExistingCopies` 跳过写回，故障现场（损坏文件）原样保留 | ✅ |
| 有效书架（不引用旧副本）+ 清单损坏 / 内容 `null` / 元素非字符串 / 文件被 `FileShare.None` 独占 → 四种变体均跳过整次清理：受保护副本与真孤儿、keepme 全部保留（快照哈希逐一相等），日志记录 skip | ✅×4 |
| 不可信清单未被改写（写回覆盖故障现场已排除；locked 变体释放锁后核对同样未改写） | ✅ |
| 有效保留清单 → 受保护副本保留、不在清单的真孤儿被清理、外来文件保留 | ✅ |

### N4（6 项）

| 用例 | 结果 |
| --- | --- |
| `{}` 启动 → 无效路径：Save 拒写、library.json 原样、副本保留并已记入保留清单 | ✅ |
| `{"Books":null}` 启动 → 同上 | ✅ |
| Books 类型错误（`"x"`）启动 → 同上 | ✅ |
| 显式 `{"Books":[]}` → 有效空书架：正常加载与保存（文件被重写为合法状态），无引用应用孤儿按白名单清理、外来文件保留 | ✅ |
| 有效旧书架 → 正文逐字恢复、Anchor=456 恢复、Save 正常且修改落盘 | ✅ |
| N1 白名单回归：引用副本保留、孤儿副本与 `.txt.tmp` 清理、keepme.txt/.dat/.tmp 保留、Anchor=456 | ✅ |

### 失败与修正记录（保留）

- 第一轮探针 2 处中止：①按场景目录查 `logs/app.log` 落空——`Diagnostics.LogPath` 在类型首次加载时固定，探针内改 `HR_LOG_DIR` 无效；②手工解析 JSON 取 Anchor 崩溃。均为探针自身问题，非应用代码；修正后 16/16。经验已并入 OPERATING-NOTES 清理条目。

## 4. OPERATING-NOTES.md 清理条目核对（已按授权回写）

「启动清理白名单」条目需补充 N3/N4 修订：②扩展为「缺失、损坏或缺少 Books 数组均不清理」；新增③清单不可信（含独占占用的造失败方法）跳过整次清理且不写回；④保留清单需登记的边界；并附探针注意（LogPath 固定）。已合并回同一条目，证据指向 `REPORT-N3-N4.md`。

## 5. 结论

- N3：**通过**（探针 10/10）。N4：**通过**（探针 6/6，含 N1 白名单回归）。
- 未验证项：无新增（本轮未涉及 GUI、DPI、多屏路径；未改动窗口与渲染代码）。
- 技术方案与历史报告（含 `REPORT-N1-N2.md`）未改动；未提交、推送或合并。工作区改动：`Library.cs`、`OPERATING-NOTES.md`、本报告。
