# RASharp

RASharp 是 RunAny 核心能力的 C#/.NET 10 + WPF 重构版本。目前聚焦以下功能：

当前版本：[v0.1.2](https://github.com/wonly211/RASharp/releases/tag/v0.1.2)

- 读取 `RunAny.ini` 和可选的 `RunAny2.ini` 树形菜单 DSL。
- 通过 Everything SDK 解析无路径程序、处理同名结果并持久化缓存。
- Windows 原生级联弹出菜单、系统托盘、菜单全局热键、菜单项热键和热字符串。
- 保存剪贴板、模拟 `Ctrl+C` 获取选中的文本或文件，再恢复原剪贴板。
- 单实例运行；重复启动会静默退出，不会创建第二套托盘、全局热键或 Everything 管理实例。

## 下载

- [自包含单文件版](https://github.com/wonly211/RASharp/releases/download/v0.1.2/RASharp-v0.1.2-win-x64-self-contained.exe)：无需安装 .NET，直接运行。
- [框架依赖单文件版](https://github.com/wonly211/RASharp/releases/download/v0.1.2/RASharp-v0.1.2-win-x64-framework-dependent.exe)：需要安装 .NET 10 Desktop Runtime x64。
- [SHA-256 校验文件](https://github.com/wonly211/RASharp/releases/download/v0.1.2/SHA256SUMS.txt)。

两个版本都面向 Windows 10/11 x64，均为单个 EXE，无需安装 RASharp。配置、缓存、日志和托管 Everything 默认保存在 `RASharp.exe` 所在目录及其子目录中。

## 运行

需要 Windows 10/11 x64。自包含版不需要预装 .NET；框架依赖版需要 .NET 10 Desktop Runtime x64。只有从源代码构建时才需要 .NET 10 SDK。

热键格式使用 `^` 表示 Ctrl、`!` 表示 Alt、`+` 表示 Shift、`#` 表示 Win。主菜单默认使用反引号键；菜单2、设置和 Everything 显示热键默认为空。

程序默认只显示托盘图标。双击托盘图标、选择“显示菜单”或使用菜单热键可以在鼠标位置打开原生菜单；悬停分类会展开子菜单。`--show` 用于启动后立即显示菜单。

RASharp 在同一 Windows 会话中只允许运行一个实例。已有实例运行时再次启动 `RASharp.exe`，新进程会以退出码 0 静默结束，现有实例继续运行。

托盘菜单中的“设置…”会打开 WPF 设置页面，也可使用 `--settings` 在启动后直接打开。当前设置页包括：

- 登录 Windows 后自动启动，并保留当前配置目录参数。
- 主菜单、菜单2和打开设置页面的全局热键。
- 启用、停用、清空并重建无路径程序缓存。
- 启用、停用 RASharp 托管的 Everything，设置显示热键、自动升级并检测连接状态。
- 管理磁盘图标缓存，清空后重新提取，或为单个程序/文件人工指定图标。
- 跟随 Windows 系统主题，或固定使用浅色、深色主题。

设置默认保存在 RASharp 程序目录下的 `Config\settings.json`，保存后会立即重新载入菜单和输入服务。

菜单图标以 PNG 统一持久化到 RASharp 程序目录下的 `Cache\RunIcon`。升级后会自动迁移并移除旧的 `MenuIcon`、`MenuIcon2` 子目录。菜单加载时优先读取缓存，未命中才调用 Windows `SHGetFileInfo`。设置页“图标”中可清空全部缓存并重新提取，也可从 PNG、ICO、JPG、BMP、EXE、DLL、普通文件或文件夹为指定菜单项设置覆盖图标。选择 EXE 或 DLL 时会枚举其中的全部图标资源，以缩略图和资源索引供人工选择。

### 托管 Everything

启用 Everything 后，RASharp 使用程序目录下的 `everything` 子目录。首次运行会从 [voidtools 官方下载页](https://www.voidtools.com/downloads/)获取最新的 1.4 稳定版 x64 便携包，使用官方 SHA-256 清单验证后解压，并通过 Everything 官方的 `-install-service` 参数配置系统服务。服务安装和版本升级停服需要 Windows UAC 授权。

RASharp 每 24 小时最多检查一次稳定版更新，不会自动切换到 Everything 1.5 Alpha。Everything 自身的登录启动和更新通知会关闭，由 RASharp 统一启动、显示和升级；设置页可以配置独立的 Everything 显示热键，默认留空。

如果载入了 `RunAny2.ini`，托盘会增加“显示菜单2”。也可以设置 `RASHARP_MENU2_HOTKEY` 为菜单2配置独立热键。

RASharp 会监控配置目录中的 `RunAny.ini` 和 `RunAny2.ini`。修改、创建、删除或编辑器替换保存文件后，会经过短暂防抖自动重新解析菜单，并同步刷新菜单项热键和热字符串；已经弹出的菜单会在关闭后、下一次唤出时应用新配置。若新配置暂时无法读取或解析失败，当前可用配置会继续保留。

命令路径不存在，或无路径程序名无法通过 Windows 已知位置、`PATH`、缓存及 Everything 找到时，该菜单项会被隐藏；由此变空的分类和多余分隔线也不会显示。网址、短语、按键序列和 Windows Shell URI 不受此检查影响。

如果没有指定配置目录，程序使用 `RASharp.exe` 所在目录下的 `Config` 文件夹，并在首次启动时生成最小示例配置。RASharp 不会读取、创建或写入 `%APPDATA%` 与 `%LOCALAPPDATA%` 中的自身数据。

## 构建和测试

从源代码构建需要 .NET 10 SDK 10.0.301 或兼容的更新功能版本。

```powershell
dotnet restore RASharp.sln
dotnet test RASharp.sln -c Release
dotnet build RASharp.sln -c Release
dotnet publish src/RASharp.App/RASharp.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/publish/win-x64
```

发行包使用单文件捆绑。WPF/.NET 原生组件会在启动时解压到 `%TEMP%\.net`；RASharp 的配置、缓存、日志和托管 Everything 仍只保存在 `RASharp.exe` 所在目录。

## 当前 DSL 支持

- `-分类`、`--二级分类` 等任意层级分类。
- `-分类|txt ini ...` 分类选择器的解析和保留。
- `别名|命令`、无别名命令、分隔符、注释。
- 别名后的 Tab 全局热键，例如 `百度\t!b|https://...`。
- 末尾 `;`/`;;` 的短语。
- AHK 风格热字符串，例如 `邮箱:*:mail|name@example.com;`。
- `%A_ScriptDir%`、常用 Windows/环境变量、日期时间变量、`%s` 和 `%getZz%`。`%APPDATA%` 与 `%LOCALAPPDATA%` 明确不展开。

当前版本只实现热字符串最核心的立即触发 `*` 与结束字符触发语义；尚未覆盖 AHK 热字符串的全部高级选项。分类选择器已经进入模型，但尚未根据选中文件扩展名隐藏菜单分类。

## 项目结构

- `src/RASharp.Core`：DSL、变量展开、命令解析、Everything 候选排序和缓存。
- `src/RASharp.Windows`：原生级联菜单、图标提取、Everything SDK、热键/键盘钩子、SendInput、剪贴板和执行器。
- `src/RASharp.App`：WPF 应用宿主、系统托盘与应用生命周期。
- `tests/RASharp.Core.Tests`：核心兼容性测试。
- `third_party/Everything`：官方 Everything SDK x64 DLL及其许可证。

运行日志默认位于 `Config\RASharp.log`，Everything 解析缓存默认位于 `Config\everything-cache.json`。所有配置、缓存、托管 Everything 和日志均保存在 `RASharp.exe` 所在目录及其子目录中。
