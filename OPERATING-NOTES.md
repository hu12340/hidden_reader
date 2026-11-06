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

### PowerShell 5.1 中文脚本必须保存为 UTF-8 with BOM（2026-11-06 复验）
- 适用：所有含中文的 `.ps1`。AI 的文件工具默认写 UTF-8 无 BOM，新写脚本运行前必须转换；纯 ASCII 驱动脚本豁免。无 BOM 时按系统 ANSI 解析，中文字符串/正则全部乱码并产生解析错误（如「字符串缺少终止符」，脚本整体不执行，编辑器中显示正常）。
- 关键步骤：`[IO.File]::WriteAllText($path, (ReadAllText ...), (New-Object Text.UTF8Encoding $true))`；批量转换建议写一个纯 ASCII 的驱动脚本逐个改写目标脚本，避免在 shell 内联命令中转义路径与反斜杠（内联转义错误会另产生空杂散文件）。
- 证据：`.run/rvw/convert.ps1`（驱动脚本）与 `.run/rvw/test-rvw1.ps1`（转换后成功运行，结果 `rvw1-result.txt`）。

### SetCursorPos 偶发不生效，鼠标点击前必须校验坐标
- 适用：`tools/gui.ps1` 的 `Mouse-Click`/`Mouse-DownAt`（已内置 `Move-CursorVerified` 重试）。
- 关键步骤：`SetCursorPos` 后用 `GetCursorPos` 比对，最多重试 4 次。
- 症状（若绕过校验）：点击落在光标旧位置，首次点击后应用"无反应"，被误判为应用缺陷。
- 证据：`REPORT-D1-D5.md` §3.3 失败复验记录。

### TextBox 无选区时 Ctrl+C 是空操作；UIA SetFocus 对 WPF TextBox 不可靠
- 适用：验证复制功能、文本框交互。
- 关键步骤：先真实点击文本框（`Mouse-Click` 中心点）获取焦点，再 Ctrl+A、Ctrl+C；断言剪贴板内容而非焦点归属。
- 证据：`REPORT-D1-D5.md` §3.3。

### WM_CLOSE 不退出应用：ShutdownMode=OnExplicitShutdown（2026-11-06）
- 适用：脚本化验收中「关闭主窗口/退出应用」步骤；基线 73b0666 与 feat/large-book-reading 均如此。
- 事实：`App.xaml` 设 `ShutdownMode="OnExplicitShutdown"`，`PostMessage(WM_CLOSE)` 后主窗口销毁但进程驻留（空书库与大书库均实测 10–15 秒不退出）。进程退出只经浮窗菜单「退出」的 `Application.Shutdown()` 路径。
- 关键步骤：脚本验收用 `Stop-Process -Id` 结束实例（阅读锚点在翻页/滚动/跳页时即时落盘，强杀不丢进度）；「退出后重启」场景可改状态后重启，或用强杀代替正常退出并在报告中注明。

### 自绘 FrameworkElement 无 UIA 矩形，点击坐标从宿主窗口推算（2026-11-06）
- 适用：对 `ReadingView`（自定义 FrameworkElement，无 AutomationPeer）做鼠标自动化。
- 症状：UIA 取 `BoundingRectangle` 为空，`SetCursorPos(0,0)` 式点击全部落空且无报错。
- 关键步骤：用主窗口 `BoundingRectangle` 推算正文区（约窗口 62% 宽、55% 高处，避开顶部工具栏与目录栏）；窗口位置在显示器右侧时坐标为物理像素，直接可用。

### FindWindow 按类名 + null 标题查不到浮窗（2026-11-06）
- 适用：查找 `HiddenReaderFloatWindow`（WS_POPUP + WS_EX_TOOLWINDOW，标题「HiddenReader 浮窗」）。
- 事实：`gui.ps1` 的 `FindWindow(cls, $null)` 实测返回 0；传完整标题能命中。
- 关键步骤：优先用 `Get-ProcessWindows($pid)` + `Get-WindowClass` 轮询筛选类名，最稳。

### 显示缩放无法用注册表即时切换
- 适用：需要 150% 缩放实测时。
- 事实：`HKCU\Control Panel\Desktop\PerMonitorSettings\<monitor>\DpiValue` 写入 + `WM_SETTINGCHANGE` 广播后 `GetDpiForSystem` 不变（需注销或经系统设置更改）。2026-11-05 实测：DpiValue=2 后系统 DPI 仍 96，已回滚。
- 建议：150% 场景请用户手动切换缩放后再跑脚本；脚本用 `GetDpiForWindow` 自证当前 DPI。
- 影响范围：仅改注册表值，回滚即无副作用；不涉及显示驱动。

### PowerShell 5.1 的 BinaryWriter.Write 会把 byte[] 重载解析成 Write(bool)
- 适用：PowerShell 手工拼二进制文件（如 .ico 装配）时。
- 症状：`$bw.Write($byteArray)` 每个数组只写入 1 个 0x01 字节——绑定到了 `Write(bool)`，非空数组被转为 true。
- 关键步骤：显式写 `$bw.Write([byte[]]$array)`。
- 证据：`tools/Make-Icon.ps1`（修复后 ico 50300 字节）；修复前仅 159 字节，2026-11-06 外观轮实测。

### SetCursorPos 在屏幕边缘被截停，边缘缩放自动化须先把窗口移离屏幕边
- 适用：对贴边窗口做 `gui.ps1` 边缘缩放自动化。
- 症状：从右边缘向外拖 80px 实测窗口只增大 27–40px，误判为应用缺陷；`Move-CursorVerified` 返回的目标坐标永远是屏幕右缘-1。
- 关键步骤：缩放测试前先 Ctrl+左键把浮窗拖离屏幕边缘，再取边缘坐标；或改拖不贴屏幕的那条边。
- 证据：`.run/appearance/resize-diag3.ps1`（cursorX 恒为 3439 = 屏幕右缘-1）；`test2-float.ps1` 移窗后 80px 全量生效。

### DWM 标题栏文字颜色切换需 SWP_FRAMECHANGED 才立即重绘
- 适用：用 `DWMWA_TEXT_COLOR`（36）做激活/失焦标题文字变暗时。
- 事实：只调 `DwmSetWindowAttribute` 不触发非客户区重绘，颜色变更不生效；补一次 `SetWindowPos(SWP_FRAMECHANGED|NOMOVE|NOSIZE|NOZORDER|NOACTIVATE)` 后立即生效。
- 关键步骤：激活 #E4E2DC / 失焦 #A8A6A0（本轮实测值），背景 `DWMWA_CAPTION_COLOR`(35) + `DWMWA_USE_IMMERSIVE_DARK_MODE`(20) 固定深色。
- 浅色主题验证：`AppsUseLightTheme=1` + 广播 `WM_SETTINGCHANGE "ImmersiveColorSet"` 立即生效，测完立即还原；标题栏颜色保持不变（2026-11-06 外观轮实测，见 `.run/appearance/`）。
- 证据：`src/HiddenReader.App/UI/DarkTitleBar.cs`；截图 `.run/appearance/shots/k3/k4-titlebar-lighttheme-*.png`。

### 浮窗右键菜单与取色的自动化（2026-11-06，阅读配色轮）
- 适用：自动化验证浮窗右键菜单项、屏幕取色与配色保存失败场景。
- 关键步骤：①右键菜单是 `TrackPopupMenu` 原生弹窗，右键后用 `keybd_event` 发 Down 逐项移动（分隔符自动跳过；当前项序：置顶、背景 0/50/100、边框 0/50/100、阅读配色…、返回普通模式、退出——「阅读配色…」需 8 个 Down 后 Enter），菜单项新增时要重数；②配色面板是 WPF 窗口，UIA 可用 x:Name 作为 AutomationId 定位（主窗口在浮窗模式下隐藏，只能从进程窗口枚举里找含 `ConfirmButton` 的窗口）；③取色验证先另起一个独立 PowerShell WinForms 无边框窗口（如 #123456）当已知色块，吸管取样后比对回填色值，截图确认 HUD；④保存失败探测用 `FileShare.None` 独占持有 `library.json`（同 N1 探针手法），点「确定」后用前台 #32770 + Enter 关提示框。
- 证据：`.run/colors/`（shot-08～shot-19b 截图、uia.ps1/prep.ps1/holdstate.ps1）；实现提交见 feat/reading-colors 阅读配色轮。
