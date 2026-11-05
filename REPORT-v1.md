# HiddenReader 首版验收报告

日期：2026-11-05　环境：Windows 11，屏幕 3440×1440（100% 缩放；原型阶段另做过 150% 验证）

## 1. 交付范围

- **离线 TXT 书架**：导入（复制副本到 `%APPDATA%\HiddenReader\books`）、移除（只删副本，原文件不动）、按书记忆进度。
- **普通阅读模式**：单击翻页 / 方向键翻页（仅持焦点时）/ 目录检测与跳转 / 全书搜索与高亮 / 跳页 / 拖动选择文字（Ctrl+C 复制）/ 重排保持阅读位置。
- **极简浮窗模式**：非分层 Win32 窗口 + DirectComposition；置顶开关；背景/边框不透明度独立 0%/50%/100%；完全透明时仍可单击翻页、右键菜单、Ctrl+拖动移动、边缘缩放；Esc 返回普通模式。
- **状态持久化**（`library.json`）：书架、每书进度、窗口位置、上次模式；重启恢复上次书籍/位置/模式（浮窗模式直接恢复浮窗及其设置）。
- 默认暗黑主题；主窗口首启位于工作区右侧，浮窗首启位于工作区右下角。

## 2. 浮窗技术方案（原型阶段结论）

WPF 分层窗口（WS_EX_LAYERED）无法满足"完全透明像素仍可接收鼠标命中"，最终采用：

- 非分层 Win32 窗口（`WS_POPUP`，`WS_EX_NOREDIRECTIONBITMAP`，`WS_EX_TOOLWINDOW`，可选 `WS_EX_TOPMOST`）；
- D3D11 交换链经 **DirectComposition** 合成到 HWND，`AlphaMode.Premultiplied`，带 Alpha 显示；
- 窗口完整接收鼠标消息：完全透明像素照样命中，单击=翻页、右键=菜单、Ctrl+左键拖动=移动、窗口边缘 6px=缩放；
- 原型验证于 100% 与 150% 缩放全部通过（含 WM_DPICHANGED、ResizeBuffers 重排）。

## 3. 原型验收结果（阶段 1，全部通过）

| 项目 | 100% | 150% |
| --- | --- | --- |
| 置顶切换 | ✅ | ✅ |
| 背景/边框独立 0%/50%/100% | ✅ | ✅ |
| 全透明时单击=翻页 | ✅ | ✅ |
| 右键菜单 / Ctrl+拖动移动 / 边缘缩放 | ✅ | ✅ |
| 选择/拖动/缩放无误触翻页 | ✅ | ✅ |
| 模式切换保持阅读位置，Esc 退出 | ✅ | ✅ |

## 4. 首版自动化验收（32/32 通过）

证据目录 `artifacts\`（截图）与 `%TEMP%\hr-test-data*\logs\app.log`（应用日志）、`library.json`（状态）。

### 4.1 书架与导入（tools\test-app1.ps1，21/21）

| 验收项 | 结果 | 证据 |
| --- | --- | --- |
| 全新启动（隔离数据目录） | ✅ | app-start，UIA 命中主窗口 |
| 导入 TXT：文件对话框可选文件 | ✅ | open-dialog（Win32 自动化对话框） |
| 导入产生副本（原文件不动） | ✅ | import-copy-created，books\*.txt 出现 |
| 书架自动选中并打开新书 | ✅ | book-in-shelf，页码标签"第 1/17 页 第1章" |
| 单击正文翻页 | ✅ | click-next-page 第1→2页（app-01-normal.png） |
| 方向键 →/↓ 前翻、←/↑ 后翻 | ✅ | arrow-* 四项（有焦点时） |
| 目录检测（12 章）并跳转 | ✅ | chapter-jump，跳后标签含章名 |
| 全书搜索（12 处）与命中导航 | ✅ | search-found / search-next-hit（app-03-search.png） |
| 跳页（输入页码回车） | ✅ | jump-to-page → 第 1/17 页 |
| 进入浮窗（主窗口隐藏） | ✅ | 主窗口标题变为"HiddenReader 浮窗" |
| 浮窗菜单调背景/边框至 0% | ✅ | app-04/05 截图，透明仍清晰 |
| 全透明浮窗单击仍翻页 | ✅ | 浮窗期翻页有效 |
| Esc 返回普通模式 | ✅ | esc-return-normal |
| 模式切换保持阅读位置 | ✅ | position-kept-after-mode-switch |
| 浮窗菜单"退出" | ✅ | exit-from-float，进程结束 |
| 浮窗模式退出后 LastMode=float | ✅ | state-lastmode-float（library.json） |
| 重启直接恢复浮窗 | ✅ | float-restored-on-start（app-07 截图） |
| 原文件改名后副本仍可打开 | ✅ | copy-opens-after-rename（改名→重启→正常阅读） |
| 移除书保留原文件 | ✅ | remove-keeps-original |
| 移除书删除副本 | ✅ | remove-deletes-copy |

### 4.2 需求 §7 补充场景（tools\test-app2.ps1，11/11）

| 验收项 | 结果 | 证据 |
| --- | --- | --- |
| 跨章节搜索并导航到后文章节 | ✅ | cross-chapter-search 第1页→第5页第4章（app2-01 截图，命中高亮） |
| 焦点切走后方向键不翻页 | ✅ | focus-loss-no-paging（最小化窗口后方向键无效果） |
| 拖窄再放大，阅读位置保持 | ✅ | resize-keeps-position（锚点归一化后继续翻页） |
| 无章节 TXT 正常翻页 | ✅ | no-chapter-paging book3 第2→3页 |
| 无章节 TXT 正常搜索 | ✅ | no-chapter-search |
| 浮窗 Ctrl+拖动移动 | ✅ | float-ctrl-drag-move（位移 -120,-150 精确生效） |
| 浮窗边缘缩放 | ✅ | float-edge-resize（宽 460→560） |
| 浮窗缩放后单击仍翻页 | ✅ | float-click-after-resize（锚点 0→200） |
| 浮窗 F5/F6/F7（背景/边框/置顶） | ✅ | float-fkeys-settings（状态落盘验证） |
| 浮窗翻页进度持久化 | ✅ | 状态写入 PerBook.b3.Anchor |
| 浮窗退出→重启恢复位置+进度 | ✅ | float-exit-restart-restore（app2-05 截图） |

### 4.3 视觉取证

- `artifacts\final-01-normal-right-dark.png`：主窗口暗黑主题、位于屏幕右侧。
- `artifacts\final-02-float-bottomright.png`：极简浮窗位于屏幕右下角，正文清晰。
- 其余过程截图见 `artifacts\app-*.png`、`app2-*.png`。

## 5. 环境与复现命令

```powershell
# 构建（先停掉运行中的实例）
Get-Process HiddenReader,FloatPrototype -ErrorAction SilentlyContinue | Stop-Process
dotnet build src/HiddenReader.App/HiddenReader.App.csproj

# 生成测试书籍（含章节书×2、无章节书×1）
powershell -ExecutionPolicy Bypass -File tools\Make-TestBooks.ps1

# 验收（隔离数据目录，不污染真实数据）
powershell -ExecutionPolicy Bypass -File tools\test-app1.ps1   # 21/21
powershell -ExecutionPolicy Bypass -File tools\test-app2.ps1   # 11/11（先重跑 Make-TestBooks）

# 运行应用
src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe
# 数据目录： %APPDATA%\HiddenReader（可用环境变量 HR_DATA_DIR / HR_LOG_DIR 重定向）
```

## 6. 已知事项与边界

- 仅支持离线 TXT（UTF-8/GB18030 自动识别），不含翻译、其他格式、联网与全局热键（按需求排除）。
- 浮窗不透明度档位为 0%/50%/100%（F5/F6 循环或右键菜单选择）。
- 搜索为全书子串匹配（大小写不敏感），命中列表支持上一处/下一处导航与高亮。
- 测试脚本依赖可交互桌面；本次验收全程在解锁的物理桌面上以真实键鼠注入完成（期间一次桌面锁定的环境故障已由用户解除后重跑）。

## 7. 结论

首版功能范围全部实现；原型 6 类验收项与首版 32 项自动化验收全部通过，无阻塞项、无失败项。
