# D1–D5 审查修复交付报告

分支：`feature/first-release`（HEAD `fa72cee`，与审查基线一致，开工时工作区干净）
日期：2026-11-05　环境：Windows 11，单显示器 3440×1440，100% 缩放（150% 见 4.3）

## 1. 修改位置

| 项 | 文件 | 内容 |
| --- | --- | --- |
| D1 | `src/HiddenReader.App/Library/Library.cs` `ReadBook` | 改为按字节读取：BOM 优先（UTF-8/UTF-16LE/BE）；无 BOM 时先用 `UTF8Encoding(throwOnInvalidBytes:true)` 严格解码，`DecoderFallbackException` 再回退 GB18030。原实现依赖 StreamReader 默认替换回退，无效字节被静默换成 U+FFFD，GB18030 永远不会被尝试 |
| D2 | 同上 `Load/Save/Import/Remove`，新增 `CleanupOrphans`、`_stateInvalid`、`_saveLock` | ①导入先写 `books/<id>.txt.tmp`，完整落盘后改名为正式副本，再提交书架条目；提交失败回滚内存与副本并抛出异常（界面弹"导入失败"，不误报成功）。②状态保存先写 `library.json.tmp` 再 `File.Replace`（首次保存用 `File.Move`），返回是否成功。③移除先提交书架变更，成功后才删副本；提交失败恢复内存（条目、PerBook 进度、LastBookId），副本保留。④状态文件损坏时置 `_stateInvalid`，`Save` 拒绝写入（保留原文件），清理跳过。⑤`CleanupOrphans`（ctor 内、成功读取后调用）只删 `books/*.tmp` 与无书架条目引用的副本 |
| D3 | `src/HiddenReader.App/UI/MainWindow.xaml.cs` `OnLoaded`；新增 `src/HiddenReader.App/UI/WindowPlacement.cs` | `AppState.WindowX/WindowY` 改为 `double?`（null=尚未保存）；兼容旧版 `-1/-1` 哨兵（`WindowPlacement.IsUnplaced`）。放置后在物理像素坐标系用 `MonitorFromRect+GetMonitorInfo` 核对窗口与最近显示器工作区交集（两轴 ≥80px 视为可见），不可见则回退移入该工作区。`SaveWindowState` 不变，落盘真实坐标（负坐标合法） |
| D4 | `src/HiddenReader.App/Float/FloatWindow.cs` 构造函数 | 浮窗恢复同规则：`-1/-1`=未放置（右下角），其余坐标（含负值）按物理像素核对其最近显示器工作区可见性，不可见时 `ClampIntoWorkArea` 回退。共享 `WindowPlacement` 统一坐标转换 |
| D5 | `src/HiddenReader.App/UI/ReadingView.cs`；`MainWindow.xaml.cs OnPreviewKeyDown`；`FloatWindow.cs OnLeftUp/OnKeyDown` | ①拖动选字松手后保留高亮（原实现把选区清空）。②翻页（单击/方向键）清除旧选区；换书走 `SetSession`/重建浮窗，同样清空。③普通模式新增 Ctrl+C：`Reader.IsKeyboardFocusWithin` 且有选区时复制 `GetSelectedText()`；搜索框/跳页框聚焦时提前返回，不拦截系统编辑行为。④浮窗 Ctrl+C 原有路径保留；移动/缩放松手不翻页（原有行为不变，选区保留） |

新增文件：`src/HiddenReader.App/UI/WindowPlacement.cs`、`tools/test-fixes.ps1`（复验脚本）。临时探针 `tools/Probe/`（D1/D2 非_GUI 复验）已按约定跑完删除。

## 2. 构建与启动

```
dotnet build src/HiddenReader.App/HiddenReader.App.csproj          # 退出码 0，0 错误
dotnet build src/HiddenReader.App/HiddenReader.App.csproj --no-incremental
    # 退出码 0，2 条警告：MainWindow.xaml.cs(152,26) CS8600 —— 基线已有
    # （string chapter = CurrentChapterTitle(); 本轮未改动该行，不属本轮范围）
启动：src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe
    （复验时 HR_DATA_DIR/HR_LOG_DIR 指向 %TEMP%\hr-dfix-100、%TEMP%\hr-d1d2-probe）
```

## 3. 复验结果

### 3.1 D1（tools/Probe，非 GUI，退出码 0）

| 用例 | 结果 |
| --- | --- |
| 无 BOM GB18030 中文样本，`ReadBook` 输出与预期逐字相等 | ✅ |
| 无 BOM UTF-8 中文样本，逐字相等 | ✅ |
| 带 BOM UTF-8 中文样本，逐字相等 | ✅ |

### 3.2 D2（同上探针，退出码 0）

| 用例 | 结果 |
| --- | --- |
| 正常导入：副本落盘 + 书架条目提交 | ✅ |
| 正常移除：书架先提交、副本删除、原文件保留 | ✅ |
| 状态提交失败（library.json 只读）→ 导入抛异常、内存与副本回滚、无残留文件 | ✅ |
| 状态提交失败 → 移除返回失败、条目/进度/副本恢复 | ✅ |
| 损坏状态读取 → `Save` 拒绝写入，磁盘损坏文件原样保留 | ✅ |
| 损坏状态下清理跳过（副本不误删） | ✅ |
| 旧格式书架与进度（Anchor=456）恢复；旧书 GB18030 正文逐字相等 | ✅ |
| 启动清理：无引用副本与 `*.tmp` 删除，被引用副本保留 | ✅ |

### 3.3 D3/D4/D5（tools/test-fixes.ps1，100% 缩放，16/16，退出码 0）

| 用例 | 结果 | 证据 |
| --- | --- | --- |
| D3 首次放置于工作区右侧（rect=2524,416，屏幕宽 3440） | ✅ | 位置核对 |
| D3 首次放置坐标立即落盘（WindowX=2524） | ✅ | library.json |
| D3 已保存位置 (100,100) 精确恢复 | ✅ | GetWindowRect |
| D3 保存位置不可见 (-1000,200) → 回退到 (8,200) | ✅ | fix-01-d3-fallback.png |
| D5 普通模式拖选松手：页码不变（高亮见截图） | ✅ | fix-02-d5-normal-selection.png |
| D5 普通模式 Ctrl+C：剪贴板=正文连续子串「，再落进水洼。她想」 | ✅ | 与 test-book-1.txt 比对 |
| D5 普通模式单击仍翻一页（第1→2页） | ✅ | 页码标签 |
| D5 搜索框 Ctrl+A/Ctrl+C 剪贴板=「挂钟」，未被拦截 | ✅ | 剪贴板比对 |
| D4 浮窗首次放置右下角（2956,1008） | ✅ | 位置核对 |
| D4 浮窗已保存位置 (800,700) 精确恢复 | ✅ | GetWindowRect |
| D5 浮窗 Ctrl+拖动移动：位移生效、锚点不变 | ✅ | library.json 锚点 |
| D5 浮窗边缘缩放 +80px：生效、锚点不变 | ✅ | 同上 |
| D5 浮窗拖选松手：锚点不变、高亮保留（截图） | ✅ | fix-03-d5-float-selection.png |
| D5 浮窗 Ctrl+C：剪贴板=正文子串 | ✅ | 剪贴板比对 |
| D5 浮窗单击翻页（锚点 0→180） | ✅ | library.json 锚点 |
| D4 浮窗保存位置不可见 (-4000,700) → 回退 (24,700) | ✅ | fix-04-d4-float-fallback.png |

失败与复验记录（保留）：第一轮 GUI 复验 13/16，三项失败——
① 首次放置坐标未落盘（`SaveWindowState` 晚于放置）→ 已修复（放置后立即落盘）；
② 可见性校验原挂在 `SourceInitialized`（早于 OnLoaded 放置，形同虚设）→ 已移至放置后执行；
③ 搜索框 Ctrl+C 用例缺陷（UIA SetFocus 未生效改真实点击；TextBox 无选区时 Ctrl+C 本就是空操作，先 Ctrl+A）→ 测试修正后通过。
另：早期用例以 (-600,200) 期望回退，属预期错误（该位置仍有 300px 可见，按 80px 阈值属"可见"，与 Windows 常规一致），改为 (-1000,200) 后通过。

### 3.4 明确标记的未验证/阻塞项

| 项 | 状态 | 原因 |
| --- | --- | --- |
| 正式应用 150% 缩放下的 D3–D5 GUI 复验 | **未验证（阻塞）** | 注册表 `PerMonitorSettings\DpiValue` + 广播无法即时生效（需注销或手动设置）。环境排查两轮已到 10 分钟预算，`DpiValue` 已回滚为 0。历史 150% 证据仅覆盖原型（提交 `efb3369` 的 `tools/test-proto-dpi150.ps1`，dpi=144 实测），**不作为本轮正式应用证据** |
| 负坐标在已连接的左侧/上方显示器的恢复 | **未验证** | 本机仅一台显示器。代码路径已由"不可见回退"用例间接覆盖（`IsUnplaced` 只把 `-1/-1` 当未放置，其他负值当有效坐标处理），但缺少真双屏实测。WindowPlacement 全程按物理像素 + 各显示器工作区判断，单屏下逻辑等价 |

## 4. 待裁定的存储路径问题

技术方案（docs/technical-design.md §本地存储与恢复）写的是 `%LocalAppData%\HiddenReader` + `state.json`；当前实现为 `%APPDATA%\HiddenReader` + `library.json`。本轮按指令**保留现状不迁移**。两者不一致需在后续（或方案回写时）裁定。

## 5. 其他说明

- 操作经验已记录到 `OPERATING-NOTES.md`（新建，依据 AGENTS.md「跨会话操作经验」条目授权）。
- 未执行任何 commit/push/merge；工作区含上述修改与新文件，待审查。
- 按 PID 清理完毕（`tools/test-fixes.ps1` 自清 + 收尾 `Stop-Process`），无本轮实例残留。
