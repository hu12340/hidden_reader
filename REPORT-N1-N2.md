# N1–N2 复审修复交付报告

分支：`feature/first-release`（开工 HEAD = `6cc09ea`「修订」，与复审基线一致，工作区干净）
日期：2026-11-06　环境：Windows 11 单显示器 3440×1440、100% 缩放

## 1. 修改位置

### N1｜限制书库启动清理（`src/HiddenReader.App/Library/Library.cs`）

- `Load`：区分三种状态——成功读取有效书架（新标志 `_shelfLoaded`，仅此情况允许清理）；状态文件缺失（记录保留清单后直接返回）；状态文件损坏（`_stateInvalid` 置位 + 记录保留清单）。
- 新增 `PreserveExistingCopies`：状态缺失/损坏时，把 books 下「应用命名的副本」写入保留清单 `library.preserved.json`。此后即使书架被重建且不引用这些副本，清理也会跳过——解决「缺失态保留的副本在下一次启动被误删」的问题。清单中被书架重新引用的条目会被移出（引用关系接管保护）。
- `CleanupOrphans` 重写为**文件名白名单**（正则 `^[0-9a-f]{32}\.txt$` 与 `^[0-9a-f]{32}\.txt\.tmp$`，与 `Import` 的 `Guid.NewGuid().ToString("N")` 命名严格一致）：
  - `32hex.txt`：仅删除「无引用且不在保留清单中」的孤儿；
  - `32hex.txt.tmp`：导入临时文件，可删；
  - 其余一切文件（`keepme.txt`、`keepme.dat`、`keepme.tmp`、任意 `*.tmp` 等）一律保留。
  - 状态缺失/损坏时不进入本方法（`_shelfLoaded` 为假），什么都不删。
- 同步修正了 `Load`/`CleanupOrphans`/`PreserveExistingCopies` 的注释。

### N2｜移除失败保留阅读现场（`src/HiddenReader.App/UI/MainWindow.xaml.cs` `OnRemove`）

- 先 `bool removed = _library.Remove(book)`，按结果分流：
  - `false`（书架提交失败，Remove 已回滚内存）：仅弹警告「书架保存失败，移除已取消。当前书籍与阅读位置未受影响…」，**不触碰** `_float`/`_currentBook`/`_session`/`Reader`/`BookSelector`，不刷新列表（避免当前书被重新打开）。
  - `true`（书架已提交；副本删除失败仅由 Library 记日志、启动清理兜底）才走原成功路径：关浮窗、清阅读区、刷新书架。
- 返回语义区分按 `Library.Remove` 原样沿用：`false`=提交失败；`true`=已提交（副本可能暂未删除）。

### N2 验证暴露的附带修复（前置显示缺陷）

复验「书架选择保留」时发现：书架下拉框（闭合态和下拉项）显示 `HiddenReader.App.Data.BookInfo` 而非书名——`DisplayMemberPath` 在自定义 ComboBox/ComboBoxItem 模板下不生效（`SelectionBoxItemTemplate` 为 null，条目模板亦未作用）。该缺陷阻塞 N2 的可见性验证，做了最小修复：

- `BookInfo` 重写 `ToString() => Title`（与 `TocItem` 同法）；移除 XAML 与 `RefreshBookList` 中不生效的 `DisplayMemberPath`；
- 闭合选择框内容改为绑定 `SelectionBoxItem.Title`（附注释说明原因）。
- 修复前后证据：`artifacts/combo-probe-open.png`（修复前类型名，已随探针清理删除，见报告留档说明）→ `artifacts/n2-03-toolbar-after-failure.png`（修复后显示 book1）。

## 2. 命令与退出码

```
dotnet build src/HiddenReader.App/HiddenReader.App.csproj                # 0 错误
dotnet build src/HiddenReader.App/HiddenReader.App.csproj --no-incremental
    # 退出码 0；2 条警告：MainWindow.xaml.cs(162,26) CS8600 —— 基线已有
    #（string chapter = CurrentChapterTitle(); 本轮未改该行；行号因 OnRemove 增行而移动）
临时探针：dotnet run --project tools/Probe/Probe.csproj        # N1，退出码 0，跑完已删
GUI 复验：powershell -ExecutionPolicy Bypass -File tools\test-n2.ps1    # 退出码 0
回归：    tools\test-app1.ps1（21/21）、tools\test-app2.ps1（11/11）
隔离目录：HR_DATA_DIR=%TEMP%\hr-n1-probe / hr-n2-test / hr-test-data*；HR_LOG_DIR 同目录 logs
```

本轮启动的实例均按 PID 清理（脚本 `$myPids` 自清 + 收尾 `Stop-Process`），无残留。

## 3. 复验结果

### N1（tools/Probe 探针，直接调用 Library 真实实现，6/6 通过）

| 用例 | 结果 |
| --- | --- |
| 有效旧书架 + 引用副本 + 应用命名孤儿 + `32hex.txt.tmp` + `keepme.txt/.dat/.tmp` → 只清白名单孤儿与临时文件 | ✅ |
| 旧书可读取（正文逐字相等）、进度 Anchor=456 保留 | ✅ |
| library.json 缺失 → 副本与外来文件全部保留，保留清单写入 | ✅ |
| 缺失态会话内导入（状态被覆盖为有效）→ 重启后旧副本不被误删、新副本被引用、外来文件保留 | ✅ |
| 保留清单中的副本被书架重新引用后退出清单 | ✅ |
| library.json 损坏 → 副本与外来文件保留、Save 拒写且损坏文件原样、重启后仍保留 | ✅ |

### N2（tools/test-n2.ps1，正式应用 GUI，10/10 通过）

| 用例 | 结果 | 证据 |
| --- | --- | --- |
| 前置：打开书、翻到第 2 页 | ✅ | [第 2/17 页　第1章　灯下的窗户] |
| library.json 只读 → 点移除 → 确认 → 失败提示弹出（应用日志 `remove rolled back`） | ✅ | `artifacts/n2-01-remove-failed-warning.png` |
| 失败后阅读区/页码/章节保留不变 | ✅ | 前后标签一致 |
| 失败后书架条目仍在（library.json 含 bid 与 Anchor）、副本仍在、下拉框仍选中 book1 | ✅ | `artifacts/n2-03-toolbar-after-failure.png` |
| 解除只读 → 重试移除 → 书架条目移除 | ✅ | library.json 不再含 bid |
| 副本删除、原 TXT 逐字节保留（哈希一致） | ✅ | Get-FileHash 对比 |
| 成功后阅读区清空（页码标签为空） | ✅ | `artifacts/n2-02-remove-success.png` |

### 回归（历史验收，确认 N2 附带修复无副作用）

- `test-app1.ps1`：**21/21 通过**（含 `book-in-shelf selection='test-book-1'`，下拉显示文本已正确）
- `test-app2.ps1`：**11/11 通过**

### 失败与复验记录（保留）

- N2 GUI 首轮 9/10：`n2-selection-kept` 读到类型名。三轮定位后确认不是「选区丢失」，而是前置显示缺陷（见 §1 附带修复）；修复后该检查经下拉列表实际列表项判定通过。
- 弹窗自动化排障（计环境排障预算）：UIA 按 `#32770`/PID 遍历查不到 MessageBox、`FromHandle` 后子树为空 → 改为前台句柄检测 + Enter 默认按钮 + 截图取证。经验已写入 `OPERATING-NOTES.md`。

## 4. OPERATING-NOTES.md 核对结论（已回写）

「测试书样例必须在 Library 首次构造之后放入书库目录」条目**已失效**：N1 白名单后，非应用命名的样例文件永远不会被清理，顺序无关紧要。已改写为「启动清理白名单」条目并列出新结论（缺失态保留、保留清单、有效书架下应用命名孤儿的边界）；证据指向本报告。另新增两条本轮验证的经验（MessageBox 自动化、DisplayMemberPath 失效）。

## 5. 存储路径及文件名裁定（单列，未变更）

- 存储位置仍为 `%APPDATA%\HiddenReader`（技术方案写 `%LocalAppData%\HiddenReader`），状态文件仍为 `library.json`（方案写 `state.json`）——与 D1-D5 轮相同，待裁定。
- 本轮新增文件名：`library.preserved.json`（保留清单，位于 RootDir，应用管理；损坏时重建，非关键数据）。技术方案未提及，需一并裁定（见 §6）。

## 6. 技术方案「启动清理」段落拟改措辞（待授权回写）

现文（docs/technical-design.md 第 36 行末句）：

> 启动时可在成功读取书架后清理未被引用的书库副本和未完成的临时文件。

拟改为：

> 启动时仅在成功读取有效书架后按文件名白名单清理：删除无引用且不在保留清单中的书籍副本（32 位十六进制 ID 命名的 .txt）和未完成的导入临时文件（同名 .txt.tmp）；其他文件一律不动。状态文件缺失或损坏时不做清理，此时发现的应用命名副本记入 library.preserved.json 保留清单，后续启动即使书架不引用它们也不删除，直到被书架重新引用。

## 7. 结论

- N1：**通过**（探针 6/6）。N2：**通过**（GUI 10/10，含重试成功路径）。回归 21/21 + 11/11。
- 未验证项：无新增。多显示器与 150% 缩放不在本轮范围且未涉及相关代码路径（本轮未改动窗口/坐标逻辑）。
- 未提交、未推送；工作区改动为 `Library.cs`、`MainWindow.xaml`、`MainWindow.xaml.cs`、`tools/test-n2.ps1`（新）、`OPERATING-NOTES.md`、本报告。
