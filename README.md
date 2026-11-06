# HiddenReader

HiddenReader 是一款面向 Windows 11 的本地 TXT 小说阅读器，使用 C#、.NET 10 和 WPF 开发。普通模式提供书架、章节目录和全文搜索。极简浮窗只显示正文，支持置顶，并可分别调节背景和边框的不透明度。

应用离线管理导入的 TXT 副本，自动保存每本书的阅读进度，并在下次启动时恢复上次的书籍、阅读位置和窗口模式。

## 从源码启动

准备 Windows 11 和 .NET 10 SDK。在项目根目录打开 PowerShell，运行以下命令。

```powershell
dotnet run --project .\src\HiddenReader.App\HiddenReader.App.csproj
```

首次运行会还原 NuGet 依赖。窗口打开后，点击「导入 TXT」并选择本地小说即可阅读。书库和阅读设置默认保存在 `%LOCALAPPDATA%\HiddenReader`。
