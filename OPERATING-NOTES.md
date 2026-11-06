# 跨会话操作经验

只收录已验证、可减少试错的方法。复用前核对：分支/版本、工作目录、显示缩放、数据路径、进程归属。

## 测试与自动化

### 启动清理白名单（2026-11-06 起，「先构造再放样例」的限制已失效；N3/N4 修订）
- 适用：`Library` 构造期清理为白名单 + 保留清单三态 + Books 数组校验的版本（N1–N4，见 `REPORT-N1-N2.md`、`REPORT-N3-N4.md`）。
- 新结论：①非应用命名的文件（如 `testdata` 的 `test-book-1.txt`、`keepme.txt/.dat/.tmp`）永远不会被清理，样例放入顺序无关紧要；②状态文件缺失、损坏或缺少可读取的 Books 数组（`{}`、`{"Books":null}`、类型错误）时不做任何清理，应用命名的副本记入 `library.preserved.json` 保留清单，重启也不误删；③`library.preserved.json` 存在但不可信（JSON 损坏、内容 null、元素非字符串、读取失败/被占用）时跳过整次启动清理且绝不写回覆盖故障现场——探针造读取失败可用 `FileShare.None` 独占持有；④有效书架（显式 `"Books"` 数组，含 `[]`）下仍会删除「无引用且不在保留清单中」的 `32位十六进制.txt` 副本及 `.txt.tmp`——探针手工构造应用命名样例且配有效书架时，需登记进保留清单或留到记录阶段之后放入。
- 旧结论（已失效）：状态文件缺失时 books 下所有 `.txt` 都会被删除——N1 修复后不再成立。
- 证据：`REPORT-N1-N2.md` §N1（探针 6/6）；`REPORT-N3-N4.md` §复验（探针 16/16：清单不可信四变体跳过清理且故障现场保留、有效清单保护副本+清理真孤儿、三种无效状态保留现场、显式空数组为有效书架）。
- 探针注意：`Diagnostics.LogPath` 在类型首次加载时固定，探针内改 `HR_LOG_DIR` 无效，应全程共用一份日志并按出现次数差判定。

### MessageBox 自动化：UIA 子树为空，用前台句柄 + Enter
- 适用：验证应用弹出的 MessageBox（确认/提示框），PowerShell 5.1 + UIAutomationClient。
- 关键步骤：不要按 ClassName `#32770` 或从 RootElement 遍历（本机实测查不到、FromHandle 后子树也为空）；轮询 `GetForegroundWindow`（等于应用 PID 且非主窗口）拿到对话框 hwnd，`GetWindowRect` 截图取证，`keybd_event` 发 Enter 触发默认按钮（确定）。
- 限制：只能走默认按钮；需要点「取消」时改坐标点击。
- 证据：`tools/test-n2.ps1` 的 `Wait-DialogHwnd`/`Close-MessageBox`；`REPORT-N1-N2.md` §N2。

### WPF 自定义 ComboBox 模板下 DisplayMemberPath 不生效
- 适用：自定义了 ComboBox/ComboBoxItem ControlTemplate 时。
- 症状：下拉项和闭合选择框显示 `类型名`（ToString），DisplayMemberPath=xxx 被忽略；闭合框的 `SelectionBoxItemTemplate` 为 null（DisplayMemberPath 不反映到它）。
- 关键步骤：给数据项重写 `ToString()` 返回显示文本（与 TocItem 同法），并移除 DisplayMemberPath；闭合框内容绑定 `SelectionBoxItem.Title`。
- 证据：`artifacts/combo-probe-open.png`（修复前下拉项显示类型名）与 `artifacts/n2-03-toolbar-after-failure.png`（修复后显示 book1）；提交记录见 `REPORT-N1-N2.md` §N2 附带修复。

### PowerShell 5.1 中文脚本必须保存为 UTF-8 with BOM
- 适用：所有含中文的 `.ps1`。无 BOM 时按系统 ANSI 解析，中文字符串/正则全部乱码并产生解析错误。
- 关键步骤：`[IO.File]::WriteAllText($path, (ReadAllText ...), (New-Object Text.UTF8Encoding $true))`。

### SetCursorPos 偶发不生效，鼠标点击前必须校验坐标
- 适用：`tools/gui.ps1` 的 `Mouse-Click`/`Mouse-DownAt`（已内置 `Move-CursorVerified` 重试）。
- 关键步骤：`SetCursorPos` 后用 `GetCursorPos` 比对，最多重试 4 次。
- 症状（若绕过校验）：点击落在光标旧位置，首次点击后应用"无反应"，被误判为应用缺陷。
- 证据：`REPORT-D1-D5.md` §3.3 失败复验记录。

### TextBox 无选区时 Ctrl+C 是空操作；UIA SetFocus 对 WPF TextBox 不可靠
- 适用：验证复制功能、文本框交互。
- 关键步骤：先真实点击文本框（`Mouse-Click` 中心点）获取焦点，再 Ctrl+A、Ctrl+C；断言剪贴板内容而非焦点归属。
- 证据：`REPORT-D1-D5.md` §3.3。

### 显示缩放无法用注册表即时切换
- 适用：需要 150% 缩放实测时。
- 事实：`HKCU\Control Panel\Desktop\PerMonitorSettings\<monitor>\DpiValue` 写入 + `WM_SETTINGCHANGE` 广播后 `GetDpiForSystem` 不变（需注销或经系统设置更改）。2026-11-05 实测：DpiValue=2 后系统 DPI 仍 96，已回滚。
- 建议：150% 场景请用户手动切换缩放后再跑脚本；脚本用 `GetDpiForWindow` 自证当前 DPI。
- 影响范围：仅改注册表值，回滚即无副作用；不涉及显示驱动。
