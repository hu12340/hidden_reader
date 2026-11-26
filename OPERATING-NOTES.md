# 跨会话操作经验

只收录已验证、可减少试错的方法。复用前核对：分支/版本、工作目录、显示缩放、数据路径、进程归属。

## 测试与自动化

### 默认输出被运行实例占用时独立构建（2026-11-09）
- 适用：构建报告 `MSB3027` / `MSB3021`，且锁定 DLL 的进程不归本轮所有。
- 用 `dotnet build src/HiddenReader.App/HiddenReader.App.csproj -o <本轮绝对路径>/build` 生成独立可执行文件。用本轮 `HR_DATA_DIR` 和 `HR_LOG_DIR` 启动该文件，结束时先核对进程路径和 PID 再清理测试实例。
- 证据：`.run/font-size-app-5673f6bf/build/`（字号轮使用独立输出，构建成功）。

### Git Bash 里用 `$()` 捕获 GUI 启动命令的输出会挂起（2026-11-18）
- 适用：从 Git Bash 运行内部用 `start` 拉起 GUI 程序的 .cmd，并试图 `out=$(cmd //c xxx.cmd)` 捕获输出。GUI 子进程继承 stdout 管道，bash 等待 EOF 直到应用退出，外层命令超时（实测 120s），而应用本身已正常启动。
- 关键步骤：验证启动类脚本时不做输出捕获，顺序执行后用 `Get-Process` 核对进程与路径；需要留证时先直接运行，凭终端回显与进程清单取证。
- 证据：本轮 `启动HiddenReader.cmd` 验证（捕获版挂起超时但应用已启动；不捕获版与 `rebuild` 版均立即返回）。

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

### 中文脚本保存编码：.ps1 须 UTF-8 with BOM，.cmd 须纯 ASCII 或 ANSI/GBK + CRLF（.ps1 2026-11-06 复验，.cmd 2026-11-18）
- 适用：所有含中文的 `.ps1`。AI 的文件工具默认写 UTF-8 无 BOM，新写脚本运行前必须转换；纯 ASCII 驱动脚本豁免。无 BOM 时按系统 ANSI 解析，中文字符串/正则全部乱码并产生解析错误（如「字符串缺少终止符」，脚本整体不执行，编辑器中显示正常）。
- 关键步骤：`[IO.File]::WriteAllText($path, (ReadAllText ...), (New-Object Text.UTF8Encoding $true))`；批量转换建议写一个纯 ASCII 的驱动脚本逐个改写目标脚本，避免在 shell 内联命令中转义路径与反斜杠（内联转义错误会另产生空杂散文件）。
- 证据：`.run/rvw/convert.ps1`（驱动脚本）与 `.run/rvw/test-rvw1.ps1`（转换后成功运行，结果 `rvw1-result.txt`）。
- `.bat/.cmd` 补充（2026-11-18）：cmd 无 BOM 机制，按系统 ANSI/GBK 逐字节解析，且 AI 文件工具默认写 UTF-8 + LF。UTF-8 中文即使只出现在 `rem` 注释里也会破坏解析——实测某行被拆成 `'猴紱浼?rebuild' 不是内部或外部命令`，而脚本继续执行、应用照常启动，报错易被掩盖。中文 .cmd 用纯 ASCII 内容 + CRLF 换行最稳（`file` 应显示 `ASCII text, with CRLF line terminators`）；后续 AI 用文件工具重写会还原成 UTF-8 + LF，须重新 `unix2dos`。
- `.cmd` 证据：`.run/launch-selfcheck/`（仓库根 `启动HiddenReader.cmd`：UTF-8 版报错；ASCII+CRLF 版无输出，正常启动与 `rebuild` 构建分支均验证通过）。

### SetCursorPos 偶发不生效，鼠标点击前必须校验坐标
- 适用：`tools/gui.ps1` 的 `Mouse-Click`/`Mouse-DownAt`（已内置 `Move-CursorVerified` 重试）。
- 关键步骤：`SetCursorPos` 后用 `GetCursorPos` 比对，最多重试 4 次。
- 症状（若绕过校验）：点击落在光标旧位置，首次点击后应用"无反应"，被误判为应用缺陷。
- 证据：`REPORT-D1-D5.md` §3.3 失败复验记录。

### TextBox 无选区时 Ctrl+C 是空操作；UIA SetFocus 对 WPF TextBox 不可靠
- 适用：验证复制功能、文本框交互。
- 关键步骤：先真实点击文本框（`Mouse-Click` 中心点）获取焦点，再 Ctrl+A、Ctrl+C；断言剪贴板内容而非焦点归属。
- 证据：`REPORT-D1-D5.md` §3.3。

### 验证关闭主窗口后 `dotnet run` 退出（2026-11-09）
- 适用：`App.xaml` 使用 `OnMainWindowClose` 的版本。旧版 `OnExplicitShutdown` 下，`WM_CLOSE` 会销毁主窗口并留下进程（基线 `73b0666` 与 `feat/large-book-reading` 实测）。
- 关键步骤：用独立的 `HR_DATA_DIR` 和 `HR_LOG_DIR` 启动 `dotnet run` 并记录 PID。点击本轮测试窗口的右上角关闭按钮后，按 PID 核对 `dotnet` 和它启动的 `HiddenReader.exe` 均已退出，结合 `app.log` 与 `library.json` 检查关闭处理及阅读状态。
- 极简模式通过 `Hide()` 保留主窗口。桌面工具无法选中浮窗时，可用 STA 探针启动实际的 `App`，等待恢复浮窗后调用 `FloatWindow.HandleCommand(ID_EXIT)`，核对 `App.Exit`、退出码和保存状态。此方法覆盖退出命令分派，鼠标右键命中需桌面复验。
- 证据：`.run/titlebar-and-exit/result.txt`、`read-log/app.log` 和 `float-probe-rerun-log/app.log`。

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

### 浮窗内容验证：GDI 截屏 + DWM 缩略图双通道判定（2026-11-06）
- 适用：验证 `WS_EX_NOREDIRECTIONBITMAP` + DirectComposition 浮窗是否真的在渲染内容；需要区分「截屏盲区」与「渲染为空」时。
- 关键步骤：①GDI `CopyFromScreen` 截浮窗矩形；②另建宿主 WPF 窗口，`DwmRegisterThumbnail(宿主句柄, 浮窗句柄)` + `DwmUpdateThumbnailProperties`（opacity=255、fVisible，目标矩形自定）后截宿主窗口——缩略图走 DWM 后合成路径，独立于 GDI 屏幕捕获；③两通道都空 = 渲染确实为空，仅 GDI 空 = 截屏盲区。注意 PowerShell P/Invoke 的 out 参数写成 `out long`（`IntPtr` 无法用 `[ref]` 传入）。
- 限制与现状：本轮实测 35ebbeb 基线的浮窗两通道均为空白，而同日 17:07 在 35ebbeb 之前的构建上 GDI 截屏可截到浮窗内容，疑为 35ebbeb 引入的渲染回归，根因未定位；证据 `.run/menu/shots/f2-thumb.png`（feat/reader-menu）、`f3-master-thumb.png`（master 35ebbeb 对照，worktree 构建于 `.run/master-build`）、`.run/colors/shot-08-float-panel.png`（早前版本可截到）。探针脚本 `.run/menu/probe-thumb.ps1`。

### 浮窗右键菜单与取色的自动化（2026-11-06，阅读配色轮）
- 适用：自动化验证浮窗右键菜单项、屏幕取色与配色保存失败场景。
- 关键步骤：①右键菜单是 `TrackPopupMenu` 原生弹窗，右键后用 `keybd_event` 发 Down 逐项移动（分隔符自动跳过；当前项序：置顶、背景 0/50/100、边框 0/50/100、阅读配色…、返回普通模式、退出——「阅读配色…」需 8 个 Down 后 Enter），菜单项新增时要重数；②配色面板是 WPF 窗口，UIA 可用 x:Name 作为 AutomationId 定位（主窗口在浮窗模式下隐藏，只能从进程窗口枚举里找含 `ConfirmButton` 的窗口）；③取色验证先另起一个独立 PowerShell WinForms 无边框窗口（如 #123456）当已知色块，吸管取样后比对回填色值，截图确认 HUD；④保存失败探测用 `FileShare.None` 独占持有 `library.json`（同 N1 探针手法），点「确定」后用前台 #32770 + Enter 关提示框。
- 证据：`.run/colors/`（shot-08～shot-19b 截图、uia.ps1/prep.ps1/holdstate.ps1）；实现提交见 feat/reading-colors 阅读配色轮。
- 字号轮在「阅读配色…」后插入「正文字号…」。`sky.list_windows` 与 `sky.list_apps` 在本机未列出 `WS_EX_TOOLWINDOW` 的极简浮窗。可在独立 STA 探针中调用对应命令，核对实际浮窗的面板归属与排版结果。该方法覆盖命令分派，实际右键命中仍需桌面复验。证据见 `.run/font-size-app-5673f6bf/probe-result.txt`。

### 浮窗自绘菜单（WPF 窗口版）的自动化（2026-11-09，UI-SPEC 轮）
- 适用：右键菜单已改为 `FloatMenuWindow`（无边框 WPF 窗口，标题「HiddenReader 浮窗菜单」）后的版本。旧经验「右键菜单是 TrackPopupMenu 原生弹窗」的按键序列不再适用（菜单项总数与顺序都变了）。
- 关键步骤：①开菜单用真实右键（浮窗中心 0.5,0.5/0.6 处），菜单 `ShowActivated` 自动持前台；②**触发命令优先走键盘**：焦点默认在第一条命令，`↓`×N + `Enter`（顺序 0 背景→1 边框→2 配色→3 字号→4 置顶→5 返回普通→6 退出），比 UIA 枚举按钮再点 rect 更稳；③枚举按钮必须按窗口标题「HiddenReader 浮窗菜单」过滤——按「≥7 个 Button 的进程窗口」匹配会误中其他窗口（本轮实测点到无关窗口的按钮）；④坐标点击菜单按钮前确认前台是菜单窗口（前台是浮窗说明菜单已被顶掉，需重开）。
- 症状与教训：①脚本运行期间桌面前台偶发被其他应用抢占 → 菜单 `Deactivated` 自动关闭（产品正确行为）→「菜单时有时无」的间歇失败，重试前应确认前台归属而非盲目重开；②坐标表达式粗心（`(Top+Bottom)*0.6` 越过底缘）会把右键点进任务栏，表现为「菜单不开」且截图中出现无关的系统菜单——先核对点击坐标是否在浮窗 rect 内。
- 证据：`.run/ui14/run3d-result.txt`（面板草稿/回滚/提交、F5/F7、0% 点击翻页、拖动不翻页、边缘缩放 +80、Esc 层级、重启恢复全通过）、`run3e-result.txt`（菜单退出）；截图 `artifacts/ui-03/`、`artifacts/ui-11/`、`artifacts/ui-12/`。
- 补充（2026-11-10，修复复审轮）：桌面存在其他前台窗口时，`mouse_event`/`keybd_event` 的合成点击会被它**物理拦截**——`WindowFromPoint` 返回别家窗口（如 Chrome 内容区），非置顶浮窗收不到 `WM_LBUTTONDOWN`，断言表现为「点击不翻页」，易误判为应用缺陷；此时 `SetForegroundWindow`、`BringWindowToTop`、`Window.Activate()` 都可能被系统前台策略拒绝。把被测浮窗置顶（`FloatState.Topmost=true`、F7 或菜单「置顶」）即可稳定，并在点击前用 `GetForegroundWindow`/`WindowFromPoint` 记录归属；点击已确认命中而断言不动时，先核对断言读取的数据字段本身是否正确（2026-11-18 补充：翻页已生效而锚点恒 0，实为脚本读错 JSON 字段，见 `.run/esc-min/run3-console.txt`）。证据 `.run/ui-fix2/sta-probe/Program.cs`（置顶后 #5 12/12）、`.run/ui-fix2/result-float.txt`（fix5c/fix5d 的前台归属行）。

### UIA 元素 BoundingRectangle 可能为空，按钮/输入框走 Pattern 不走坐标（2026-11-09，UI-SPEC 轮）
- 适用：对本应用任意控件做自动化点击/输入时。
- 事实：`FromHandle` 包装后 `FindFirst` 得到的元素 `BoundingRectangle` 可能为 Empty（本轮 `BookButton` 实测），坐标点击会落在 (0,0) 附近；同轮同按钮换 UIA 根（桌面 RootElement 按 PID 过滤）后 rect 又有效——按 hwnd 包装的树不可靠。
- 关键步骤：按钮一律 `InvokePattern.Invoke()`；文本框用 `ValuePattern.SetValue`（WPF 会触发 TextChanged）+ 元素 `SetFocus()` 后再发 Enter；正文区/浮窗本体（自绘无 UIA）才用窗口 rect 按比例推算坐标。另：`Get-MainHwnd` 若按标题 `HiddenReader*` 匹配会误中浮窗（标题「HiddenReader 浮窗」），需先按类名排除 `HiddenReaderFloatWindow`。
- 证据：`.run/ui14/scripts/lib.ps1`（Invoke-InAppId/Set-BoxValue 封装）与 `.run/ui14/run1d-result.txt`（场景全绿）。

### WPF 弹层与模态窗口的 UIA 定位（2026-11-09，图标顶栏轮）
- 适用：对 WPF `Popup` 弹层（如顶部菜单）和应用内模态窗口（如帮助窗口）做 UIA 自动化。
- 事实与关键步骤：①弹层内复杂内容按钮（StackPanel 多 TextBlock）不暴露为 UIA Button，多半也无 Name——给按钮加 `x:Name` 用 AutomationId 定位（如 `ImportButton`、`RemoveButton`），或找其 Text 元素按中心真实点击；②弹层刚展开时 UIA 子树异步挂入，FindFirst 可能瞬时为空，需轮询约 2.5 秒再判失败；③`ShowDialog` 模态窗口不出现在 UIA 根子树（`FocusedElement` 却能返回其名称，勿据焦点亮盲信窗口存在）——改用 `Get-ProcessWindows`+标题做 Win32 枚举再 `AutomationElement::FromHandle` 包装。
- 证据：`.run/icon-menu/probe-uia.ps1`、`probe-help.ps1`（枚举对照）；`run1.ps1`+`result1.txt`（38/38，feat/icon-top-menu）。
- 补充（2026-11-09，D1 竞态修复轮，实测插桩证据）：①`StaysOpen=False` 弹层的外击自动关闭发生在触发按钮 `PreviewMouseDown` 之前（按下时读 `IsOpen` 已为 False，且后续点击中该隧道事件不再触发——原始按下被弹层捕获转发），`Popup.Closed` 事件恒定迟到约 150ms，因此 Click 处理器无法靠事件时序区分「本次按下关闭」与「早前关闭」；带时间窗的关闭标记方案在此类快速往返场景必然误判。②`PopupAnimation="Fade"` 的关闭淡出在弹层被另一弹层遮挡时动画时钟停滞，hwnd 长期不隐藏：Win32 `IsWindowVisible` 仍为 True、内容元素 `IsOffscreen=False` 且矩形有效，UIA 与命中测试都会被僵尸弹层干扰；自动化判定弹层真实可见性需沿 RawView 向上找最近带 hwnd 祖先再查 `IsWindowVisible`，且实现上应避免给此类 Popup 挂关闭动画。③可靠做法是 `StaysOpen="True"`+手动管理关闭（窗口级 `PreviewMouseDown` 区分触发图标与外击、`Deactivated`/`LocationChanged` 收起），使 `ToggleMenu` 能读到精确 `IsOpen`（实现见 `MainWindow.xaml.cs`，回归 `.run/icon-menu/toggle-race.ps1` 5/5）。

- 补充（2026-11-10，修复复审轮实测）：本应用顶栏「查看 / 设置」弹层的菜单按钮（`CommandButton` + 图标 + 单 TextBlock）既不在主窗口 UIA 子树里，按名称全进程枚举（`MainWindow` + `Get-ProcessWindows`）也找不到，`FindAll(Descendants)` 只见弹层外的顶栏按钮。可用做法：真实鼠标点击顶栏按钮（UIA 矩形中点，先 `Activate-App`），弹层持前台后直接 `Enter` 执行第一项——查看→目录、设置→阅读配色…。证据 `.run/ui-fix2/scripts/run-main.ps1`（`Toggle-Toc`）、`run-colors.ps1`（`GearButton`+Enter）。

### 边缘缩放拖动与 MoveWindow 传参（2026-11-09，图标顶栏轮）
- 适用：`gui.ps1` 系脚本做 WindowChrome 窗口边缘缩放自动化、脚本内自拼 RECT 结构时。
- 关键步骤：①按住鼠标的缩放拖动必须增量移动（每步 ~10px 间隔 60ms），单步 50px 大跳跃实测不被窗口尺寸循环跟随，dw=0 会误判为应用缺陷（`SetCursorPos` 校验通过也无济于事）；②自定义 RECT 结构（只有 Left/Top/Right/Bottom）没有 Width/Height——`MoveWindow` 传 `$null` 宽高会把窗口缩成 0 尺寸且无报错，后续所有坐标断言连环失败，宽高一律用 `Right-Left`/`Bottom-Top` 现算。
- 证据：`.run/icon-menu/probe-narrow.ps1`（增量移动 +50 生效、窄窗命中成功）对照 `run1.ps1` 早期 dw=0 记录；`result1.txt`。

### 复用历史验证脚本前，先核对脚本内的构建产物路径（2026-11-10，修复轮）
- 适用：任何复用 `.run/<旧轮>/scripts/lib.ps1` 或其中 `Start-App` 的验证轮。
- 事实：lib.ps1 的 `$script:Exe` 硬编码指向**旧轮构建目录**（如 `.run/ui14/build/`）。本轮修复后若不同步改路径，桌面验证会全程跑在**旧构建**上——出现「STA 探针全过、桌面断言异常」的分裂现象，且部分断言可能因旧行为碰巧符合而误判通过，误导排查方向（本轮实测：Exe 路径事故导致 fix-run1 首轮结论作废）。
- 关键步骤：①复用前 `grep -n "Exe" lib.ps1` 确认产物路径为本轮构建；②每轮开始用一条「行为指纹」断言校验产物版本（如本轮以 STA 探针的 `time-sentinel-field-removed` 断言或新增日志行验证新代码确实在跑）；③lib.ps1 级公共脚本被多轮复用时，把产物路径改为环境变量参数。
- 证据：`.run/ui-fix/`（修正路径前后 fix-run1 结论反转）、`.run/ui-fix/sta-probe/run.ps1`。

### PowerShell 验证脚本的几个高频坑（2026-11-10 起，多次轮次补充）
- 函数参数命名不得使用 `$input`（PowerShell 自动变量）：作为形参会静默变空，函数体按空串执行，表现为「功能整体失效但无报错」。本轮 `Jump-PageBox([string]$input)` 五连跳全部空转即此因。
- 写回 .ps1 的工具（perl -0pi、部分 sed）会**剥掉 UTF-8 BOM**：无 BOM 的中文脚本被 PowerShell 5.1 按 GBK 解析，中文锚点字符串全部失配、中文字面量乱码。凡 perl/sed 修改过含中文的 .ps1，改后必须重跑 tobom；脚本内比较/匹配中文的锚点尽量用 `[char]0xXXXX` 拼接规避。
- 证据：`.run/ui-fix/`（`$input` 事故修复前后的 run1 输出对比；BOM 修复前后 run3c 的菜单标题匹配失败与恢复）。
- 结果文件被脚本自身与 `Tee-Object` 双重占用：脚本末尾 `Set-Content <轮次>/result-x.txt` 与外层 `... | Tee-Object -FilePath <同一路径>` 并发写会抛 `IOException`，脚本退出码仍为 0，但断言文件缺失或为空。让脚本自己写结果文件，控制台输出另存到不同文件名（如 `float-console.txt`）。
- 读 `library.json` 阅读锚点用 `PerBook.<LastBookId>.Anchor`（ConvertFrom-Json 后 `$j.PerBook.($j.LastBookId).Anchor`）：`Books[]` 数组元素没有 `Anchor` 字段，按数组元素读恒得 0——表现为翻页断言假 FAIL 或 0==0 假通过（2026-11-18 实测，修正前后对比见 `.run/esc-min/run3-result.txt` 与 `run3-console.txt`）。
- 应用冷启动后立即发合成键可能整体落空（约 800ms 内，无报错、无日志）：启动后等 ≥1.5s 再发键，或让关键断言失败时自动重试一次再判；同一脚本首跑 FAIL、加等待后全绿且重试未触发即此因（2026-11-18，两次输出对比见 `.run/esc-min/run4-console.txt`）。
- 窗口最小化断言用 `IsIconic` + `IsWindowVisible` 组合：最小化窗口 `IsWindowVisible` 仍为 true，两者同时成立即「缩到任务栏且可达」；「点任务栏恢复」自动化用 `ShowWindow(SW_RESTORE)` 代理并在报告中注明差异（2026-11-18，证据 `.run/esc-min/run1-result.txt`、`run4-result.txt`）。
- 校验含中文的 JSON 不要用 `Get-Content -Raw`（无 BOM 的 UTF-8 被按 ANSI/GBK 误读，乱码吞掉引号后 ConvertFrom-Json 假报解析失败）；用 `[IO.File]::ReadAllText`（自动按 UTF-8 解码）。同理，`git show <file>` 输出经 PowerShell 管道/`Out-File` 落盘会按 console 代码页重编码、中文全部乱码，字节级提取改用 bash 重定向 `>`。两个假警报都会把有效文件误判为损坏（2026-11-18，对照见 `.run/esc-min/scripts/check-json.ps1` 与 `tokens-prev2.json`/`tokens-new2.json`）。
- 检索 .NET DLL 是否含某 UTF-16 字符串，不要用 `GetString(全部字节).Contains()`：#US 堆的字符串常落在文件**奇数字节偏移**（压缩长度前缀 1 字节所致），偶对齐解码得到错位假文本——新串假阴性、旧串可能假阳性（本轮两种扫描互相矛盾实为一因）。权威方法＝字节模式搜索：`[Text.Encoding]::Unicode.GetBytes(探针)` 后逐字节比对。中文探针用码位构造 `-join [char[]](0x65E0,...)`，含中文的 .ps1（无 BOM 时连注释里的中文都会 GBK 误析吞换行致解析错误）一律纯 ASCII；Git Bash 向 `powershell -Command` 传中文也会乱码，一律写 .ps1 文件执行（2026-11-18，`.run/esc-min/scripts/scan-dll.ps1`→`scan-dll2.ps1`→`scan-dll3.ps1` 三版演进）。
