# HiddenReader 首版技术方案

对应 [首版需求](requirements.md)。本方案选定普通界面和阅读核心的技术栈，浮窗实现以 Windows 11 原型结果为准。

## GUI 框架

选用 **C#、.NET 10 LTS 和 WPF**。首版只面向 Windows 11，WPF 可承担书架、目录、搜索和设置界面，也提供正文排版所需的 `TextFormatter`。普通窗口和极简浮窗共享当前小说、阅读位置、分页与翻页命令，窗口置顶、缩放热区和鼠标输入由各自的窗口处理。 [.NET 支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy) · [WPF 窗口](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/windows/) · [WPF 文本排版](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.textformatting.textformatter?view=windowsdesktop-10.0)

**窗口外观**。普通窗口保留系统标题栏，在获得窗口句柄后通过 Windows 11 DWM 设置标题栏背景和文字颜色。显式设置颜色使应用在系统使用浅色主题时也能显示深色标题栏，按窗口激活状态调整文字亮度。可执行文件和 WPF 普通窗口引用同一个多尺寸 `.ico`。 [Windows DWM 窗口属性](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)

**阅读配色**。主窗口的工具栏和原生浮窗的右键菜单共用一个 WPF 面板。面板以发起操作的窗口为宿主，浮窗置顶时面板仍须可操作。应用层区分已保存的配色和面板草稿，两个阅读视图通过 `PageRenderer` 已有的文字画刷参数绘制正文。普通窗口绘制不透明背景。浮窗的背景 RGB 来自配色，Alpha 来自现有背景不透明度，沿用非分层 Win32 窗口与 DirectComposition。修改配色只重绘当前页，复用现有排版结果和阅读锚点。

**屏幕取色**。取色仅在用户主动进入取色模式时运行。以光标的物理屏幕坐标读取当前桌面合成画面的像素 RGB，涵盖混合 DPI 和带负坐标的副屏。可先验证 `GetPhysicalCursorPos` 与桌面设备上下文上的 `GetPixel` 是否满足目标场景。十字光标和色值提示应避开采样位置。取色期间由临时取色交互接收确认点击和 `Esc`。确认、取消或取色异常后释放输入并恢复面板，异常时保留原草稿。 [Win32 光标物理坐标](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getphysicalcursorpos) · [Win32 像素 RGB](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getpixel)

## 浮窗原型

先用固定的两页中文正文制作独立浮窗原型。WPF 的 `AllowsTransparency` 使用分层窗口，Windows 会让零 Alpha 区域的鼠标消息穿透。因此用 WPF 无边框窗口验证基本显示和输入，再针对完全透明的缩放热区验证非分层的 Win32 窗口与 DirectComposition。后者让合成层负责显示，由窗口处理鼠标命中，并通过 `WM_NCHITTEST` 定义边缘和角落的缩放热区。浮窗采用在 Windows 11 上通过以下验证的实现。 [WPF 透明窗口](https://learn.microsoft.com/en-us/troubleshoot/developer/dotnet/framework/general/wpf-render-thread-failures) · [Win32 分层窗口](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows) · [Windows 合成窗口示例](https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/june/windows-with-c-high-performance-window-layering-using-the-windows-composition-engine) · [自定义窗口边框与命中测试](https://learn.microsoft.com/en-us/windows/win32/dwm/customframe#enabling-hit-testing-for-the-custom-frame)

| 验证场景 | 通过条件 |
| --- | --- |
| 置顶与焦点 | 置顶时切换到其他普通应用，浮窗仍可见，方向键仍交给当前应用。关闭置顶后，浮窗恢复普通窗口层级。 |
| 背景与边框透明度 | 分别测试 `0%`、`50%` 和 `100%`。两个设置互相独立，正文始终清晰。两者均为 `0%` 时，文字之外能直接看到后方窗口。 |
| 完全透明时的输入 | 单击正文只前进一页，右键可打开菜单。`Ctrl` 加左键拖动可移动窗口，拖动选字、移动或缩放后松手均不翻页。 |
| 窄边缘与 DPI | 边框为 `0%` 时仍可从边和角调整大小。在 `100%` 和 `150%` 显示缩放下验证光标与缩放热区，缩放后正文自动重排。 |
| 模式切换 | 普通模式与浮窗来回切换时保留阅读位置，浮窗可通过菜单或有焦点时按 `Enter` 退出；按 `Esc` 将整个软件最小化到任务栏。 |

浮窗显示层（DirectComposition 交换链）在布局尺寸或阅读锚点变化时重新排版并呈现。尺寸不变但锚点已前进（单击、方向键、滚轮的有效翻页/滚动）时同样必须重排，否则提交的仍是旧页画面。固定尺寸浮窗「翻页不刷新」即源于仅按尺寸判断是否重排。只改变颜色时，复用当前排版结果提交新帧。滚轮在「悬停滚动非活动窗口」开启（Windows 默认）时无需焦点即可滚动浮窗；关闭该系统设置时需先单击浮窗取得焦点。

## 正文定位与分页

用每本书的有效编码解码 TXT 副本，将换行统一为 `\n`，得到供章节识别、搜索和排版共用的正文。统一用 **正文中的 UTF-16 偏移量**定位。目录记录标题起点，搜索结果记录命中起点和长度，阅读进度记录当前阅读锚点。

WPF 和 Win32 的滚轮增量采用相同的符号约定。两个窗口都在输入层将负增量映射为向后文滚动，将正增量映射为向前文滚动，并调用共享的阅读会话移动正文锚点。 [Windows 滚轮消息](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mousewheel)

**锚点与滚动**。锚点是可视区第一行的正文偏移：翻页后为页首，滚轮滚动后可以是任意行首（可停在两页边界之间）。行断点只由段落文本和可用宽度决定，与页边界无关，因此从任意行首排版都能得到与整页排版一致的后续行。普通翻页从当前可视位置排版一整屏推进（`NextPage`），或回退一屏的行数（`PreviousPage`，通过 `Paginator.CollectLineStartsBefore` 收集锚点之前的行首）；滚轮按系统「每格滚动的行数」设置连续移动行首，显示到正文末尾后到底。锚点始终是行首，持久化与恢复直接按该偏移渲染；若恢复时的布局与保存时不同，先经 `Paginator.FindPageStartContaining` 定位回包含锚点的页首，保证锚点文字仍在视野内。

**分页与总页数**。页首列表（全书分页）在专用 STA 后台线程（`BackgroundPager`）逐页计算——行测量依赖 WPF `FormattedText`，不能在 MTA 线程进行，也不应阻塞 UI 线程；后台计算期间窗口可重绘、移动、翻页和滚动。同一时刻只保留最新请求，切书或缩放后未完成的旧计算被放弃，过期结果按会话与布局尺寸校验后丢弃。总页数未算好时界面显示阅读百分比（锚点/正文长度）；算好后显示当前布局的准确页码与总页数，并允许按页跳转（跳转使用缓存的页首列表，不在 UI 线程排版全书）。窗口尺寸变化后重新请求后台分页。

**导入与打开**。导入的副本复制与打开时的正文解码、章节识别都在后台线程执行，期间普通窗口保持可交互，并在状态栏显示当前阶段（导入 / 解码正文 / 识别章节）。正文解码完成即建立会话并显示：单页排版只依赖锚点，阅读（滚动、翻页）不等章节识别与全书分页。导入与移除仍保持副本提交、失败回滚与启动清理的原有约定。

## 本地存储与恢复

数据保存在当前用户的 `%LocalAppData%\HiddenReader`。小说副本按内部书籍 ID 命名，原文件名只用于生成默认书名。

| 位置 | 内容 |
| --- | --- |
| `books/<book-id>.txt` | 保留原始字节的 TXT 副本，便于重新选择编码。 |
| `state.json` | 格式版本、书架与每本书的编码和阅读锚点、上次阅读的书籍 ID、窗口模式、浮窗位置与大小、置顶与不透明度，以及应用级正文文字色与背景色。 |

每个用户只运行一个写入书库的实例。导入时先写完临时副本，再移至正式路径并提交书架条目。移除时先提交书架变更，再按书籍 ID 删除副本。状态写入串行执行，先写同目录临时文件，再替换正式文件。启动时可在成功读取书架后清理未被引用的书库副本和未完成的临时文件。

正文文字色与背景色以 `#RRGGBB` 字符串保存在 `AppState`，旧状态缺少任一字段时该项使用默认值。预览时草稿独立于 `AppState`，所以其他状态保存也只会写入已确认的配色。点「确定」后沿用 `Library.Save()` 原子保存。如果保存失败，恢复内存中的已确认值并提示用户，保留草稿供重试。

翻页、滚动和跳转后保存锚点，窗口状态在操作结束时保存，退出前写入尚未落盘的变更。启动时先读取状态，再打开对应模式并定位到锚点（正文解码在后台进行，就绪后按锚点渲染）。上次是浮窗时直接显示浮窗，若原显示器不可用则将窗口移入可见工作区。没有可恢复的小说时打开书架。
