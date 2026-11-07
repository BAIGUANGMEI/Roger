# Roger

Windows 硬件监控与磁盘空间分析工具。原生 C# / WPF 界面，支持亮色、暗色及跟随系统，默认每 2 秒采样一次。

[源码仓库](https://github.com/BAIGUANGMEI/Roger) · [下载正式版本](https://github.com/BAIGUANGMEI/Roger/releases/latest)

## 初版功能

- **实时监控**：CPU、GPU 使用率及可用温度，内存占用，网络上传/下载速率，磁盘容量。
- **界面外观**：统一 R 字母 Logo 和 Lucide 矢量图标，亮暗主题即时切换，紧凑滚动条，固定页面标题；传感器刷新保留列表位置。
- **硬件配置**：处理器、显卡、内存、主板、BIOS、系统、磁盘、网络设备与可读取的传感器信息。
- **监控条与托盘**：随应用启动显示监控条；可选择 CPU / GPU / 内存 / 网络、温度、精简模式、透明度和刷新间隔。支持任务栏两侧、屏幕角落或自由拖动，双击打开主窗口，右键切换位置。任务栏被激活并遮住监控条时会恢复显示层级，不激活监控条或抢走焦点。可关闭主窗口后保留托盘运行。
- **磁盘空间扫描**：选择文件夹或分区，统计直接子项和大文件，显示扫描进度，支持取消。
- **文件系统检查**：管理员模式下直接在 Roger 内运行 Windows CHKDSK 只读检查，显示中文实时报告，支持取消，全程不打开终端。
- **网络测速**：用户点击后测量到 Cloudflare 的下载、上传、HTTP 延迟和抖动。
- **版本检查**：设置 GitHub 仓库后，在应用中查询最新正式 Release 并打开下载页；可选择启动时检查更新。

## 使用

发布包面向 **Windows 10 / 11 x64**。解压完整的 `Roger-win-x64.zip` 后运行 `Roger.exe`，保留旁边的依赖文件。自包含发布包无需另装 .NET。

开发环境需要 Windows 和 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)：

```powershell
dotnet run --project src/Roger/Roger.csproj
```

编译与运行离线冒烟检查：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
```

生成自包含的 x64 便携包：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

输出位于 `artifacts/Roger-win-x64/`，压缩包为 `artifacts/Roger-win-x64.zip`，同时生成 `SHA256SUMS.txt`。配置保存在 `%LOCALAPPDATA%\Roger\settings.json`，更新采用原子替换，保留上一份 `.bak`。开启“登录时启动”只写入当前用户的 Windows 启动项。

## 数据与边界

**温度与硬件支持。** 通过 [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6) 读取传感器。CPU 等底层传感器需要 PawnIO 驱动和管理员模式；硬件页显示具体访问状态，并提供“安装官方温度驱动”和“以管理员身份重启”入口。安装只由明确点击触发，下载固定版本的官方签名驱动，先校验 SHA-256 与 Windows 数字签名，再经 UAC 授权安装；不会关闭防护或修改系统安全策略。当前版本采用 [PawnIO 2.2.0](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0)。硬件或固件没有提供有效读数时仍显示“不可用”。多 GPU 机器的总览优先独立显卡，传感器列表保留其他设备信息。更多数据来源见 [硬件说明](docs/HARDWARE.md)。

**任务栏展示。** 当前监控条是贴靠任务栏的置顶悬浮窗，带独立托盘入口；它不是 Windows 11 任务栏内部的扩展插件。可能覆盖任务栏图标，可拖到空白处、选择屏幕角落或启用精简模式。切换显示器、DPI 和任务栏布局后会重新计算位置。

**磁盘扫描。** 空间分析进行文件与文件夹统计。独立的“文件系统检查”页面以管理员权限隐藏运行 `chkdsk X:`，把输出捕获到软件内，不附加修复参数，不打开终端，不执行卸载或安排重启。对非零退出码保守显示需查看报告；活动卷可能需要空闲时复查。未实现坏道检测或自动清理。空间结果按文件的**逻辑大小**计算，稀疏文件、压缩文件、硬链接等可能与“实际占用空间”不同。跳过符号链接、目录联接等重解析点及无法访问的项目；部分扫描会说明跳过情况。扫描过程中发生的文件变动可能影响结果。

**网络。** 实时流量来自活动网络接口字节计数，显示的是本机流量；多网卡环境可能包含虚拟接口或重复转发流量。主动测速最多下载 **32 MiB**、上传 **8 MiB**，另有少量请求开销，可随时取消；不会上传用户文件。它是到 Cloudflare 的单连接 HTTPS 估算，HTTP 延迟不是 ICMP Ping，结果不代表运营商带宽上限。代理、VPN、地区连通性和其他应用会影响结果。

## GitHub 与发布

官方开源仓库为 [BAIGUANGMEI/Roger](https://github.com/BAIGUANGMEI/Roger)，附带 MIT 许可证、Windows CI 和 Release 工作流。Fork 后可以改用自己的发布仓库。

1. 在 GitHub 创建仓库并推送本项目。
2. 在版本页填写发布仓库，例如 `your-name/Roger`；默认连接 `BAIGUANGMEI/Roger`。
3. 为正式版本更新项目版本号，提交代码。标签发布流程会将标签版本传入构建；更新检查从实际程序集读取当前版本。
4. 推送 `v0.2.0` 这样的版本标签；Release 工作流会编译、运行冒烟检查、打包并附上 SHA256 校验值。

版本检查只连接所配置仓库的 GitHub Release API。初版打开官方 Release 下载页，由用户下载和替换程序；尚未实现后台下载、自动安装与回滚。GitHub 的连通性和 API 限额可能影响检查。

## 结构与许可证

```text
src/Roger/              WPF 应用、硬件采样与界面
tests/Roger.Smoke/      不依赖真实外网的行为检查
scripts/               本地编译与打包
.github/workflows/     Windows CI 与标签发布
third_party/           依赖许可证与上游声明
```

Roger 自有代码采用 [MIT](LICENSE)。LibreHardwareMonitorLib 采用 MPL-2.0，其中部分组件另有许可证；这些组件不因 Roger 的 MIT 许可证而改变授权。依赖来源及声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

