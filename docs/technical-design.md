# HiddenReader 首版技术方案

对应 [首版需求](requirements.md)。本方案选定普通界面和阅读核心的技术栈，浮窗实现以 Windows 11 原型结果为准。

## GUI 框架

选用 **C#、.NET 10 LTS 和 WPF**。首版只面向 Windows 11，WPF 可承担书架、目录、搜索和设置界面，也提供正文排版所需的 `TextFormatter`。普通窗口和极简浮窗共享当前小说、阅读位置、分页与翻页命令，窗口置顶、缩放热区和鼠标输入由各自的窗口处理。 [.NET 支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy) · [WPF 窗口](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/windows/) · [WPF 文本排版](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.textformatting.textformatter?view=windowsdesktop-10.0)

## 浮窗原型

先用固定的两页中文正文制作独立浮窗原型。WPF 的 `AllowsTransparency` 使用分层窗口，Windows 会让零 Alpha 区域的鼠标消息穿透。因此用 WPF 无边框窗口验证基本显示和输入，再针对完全透明的缩放热区验证非分层的 Win32 窗口与 DirectComposition。后者让合成层负责显示，由窗口处理鼠标命中，并通过 `WM_NCHITTEST` 定义边缘和角落的缩放热区。浮窗采用在 Windows 11 上通过以下验证的实现。 [WPF 透明窗口](https://learn.microsoft.com/en-us/troubleshoot/developer/dotnet/framework/general/wpf-render-thread-failures) · [Win32 分层窗口](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows) · [Windows 合成窗口示例](https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/june/windows-with-c-high-performance-window-layering-using-the-windows-composition-engine) · [自定义窗口边框与命中测试](https://learn.microsoft.com/en-us/windows/win32/dwm/customframe#enabling-hit-testing-for-the-custom-frame)

| 验证场景 | 通过条件 |
| --- | --- |
| 置顶与焦点 | 置顶时切换到其他普通应用，浮窗仍可见，方向键仍交给当前应用。关闭置顶后，浮窗恢复普通窗口层级。 |
| 背景与边框透明度 | 分别测试 `0%`、`50%` 和 `100%`。两个设置互相独立，正文始终清晰。两者均为 `0%` 时，文字之外能直接看到后方窗口。 |
| 完全透明时的输入 | 单击正文只前进一页，右键可打开菜单。`Ctrl` 加左键拖动可移动窗口，拖动选字、移动或缩放后松手均不翻页。 |
| 窄边缘与 DPI | 边框为 `0%` 时仍可从边和角调整大小。在 `100%` 和 `150%` 显示缩放下验证光标与缩放热区，缩放后正文自动重排。 |
| 模式切换 | 普通模式与浮窗来回切换时保留阅读位置，浮窗可通过菜单或有焦点时按 `Esc` 退出。 |

## 正文定位与分页

用每本书的有效编码解码 TXT 副本，将换行统一为 `\n`，得到供章节识别、搜索和排版共用的正文。统一用 **正文中的 UTF-16 偏移量**定位。目录记录标题起点，搜索结果记录命中起点和长度，阅读进度记录当前正文锚点。

按实际可用宽高和字号排版，记录每页覆盖的正文区间。普通翻页将锚点移到目标页首，目录与搜索跳转将锚点设为目标偏移。窗口缩放或切换模式时，重新计算页边界并显示包含原锚点的页面，保留锚点供下次启动恢复。页码取自当前排版，全书百分比按正文长度换算为偏移。

## 本地存储与恢复

数据保存在当前用户的 `%LocalAppData%\HiddenReader`。小说副本按内部书籍 ID 命名，原文件名只用于生成默认书名。

| 位置 | 内容 |
| --- | --- |
| `books/<book-id>.txt` | 保留原始字节的 TXT 副本，便于重新选择编码。 |
| `state.json` | 格式版本、书架与每本书的编码和阅读锚点、上次阅读的书籍 ID、窗口模式、浮窗位置与大小、置顶与不透明度。 |

每个用户只运行一个写入书库的实例。导入时先写完临时副本，再移至正式路径并提交书架条目。移除时先提交书架变更，再按书籍 ID 删除副本。状态写入串行执行，先写同目录临时文件，再替换正式文件。启动时可在成功读取书架后清理未被引用的书库副本和未完成的临时文件。

翻页和跳转后保存锚点，窗口状态在操作结束时保存，退出前写入尚未落盘的变更。启动时先读取状态，再打开对应模式并定位到锚点。上次是浮窗时直接显示浮窗，若原显示器不可用则将窗口移入可见工作区。没有可恢复的小说时打开书架。
