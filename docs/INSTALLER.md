# Windows 安装器

安装器面向 Windows 10 1809 / Windows 11 x64，使用 Inno Setup 6.7.4 构建。默认安装到 `Program Files\Roger`，请求管理员权限，提供开始菜单及可选桌面快捷方式，并在 Windows“已安装的应用”中注册卸载入口。向导支持简体中文和英语，跟随系统亮暗主题。

Roger 的 .NET 10 / WPF 运行时随应用自包含发布。v0.4.0 安装器同时包含自包含的单文件更新组件 `Roger.Updater.exe`，用于退出 Roger 后更新程序。安装器还离线捆绑完整的官方签名 PawnIO 2.2.0 安装程序，检测到缺失或过旧时自动执行官方 `-install -silent` 参数；已有 2.2.0 或更新版本及驱动服务时跳过。不单独拆出驱动、不安装 unrestricted 版本、不关闭 Windows 防护。

构建时同时验证 PawnIO 的固定 SHA-256 和 Windows Authenticode 发布者，执行前再次核对解压出的 SHA-256。依赖安装失败时停止并报告实际错误，不显示安装成功。官方安装器返回 3010 / 1641 时，完成页提示保存工作后手动重启，完成后不启动 Roger，静默调用返回 3010；Roger Setup 不调用系统重启，也不启用 Inno 的自动重启流程。

手动安装、升级和卸载前需从托盘菜单退出 Roger，确保监控历史正常写入。安装器使用与应用相同的进程互斥标识检测运行状态，提示退出，不强制终止。v0.4.0 的软件内更新会自动保存历史并正常退出，更新组件再运行安装器并重新打开 Roger，详见 [软件内更新](UPDATING.md)。公开的 v0.3.0 及更早版本需手动安装 v0.4.0 一次才能获得该组件。

卸载时调用应用的 `--unregister-startup` 维护参数清理当前用户的登录启动任务与旧注册表入口。卸载保留 `%LOCALAPPDATA%\Roger` 中的设置、提醒、历史和优化记录，也保留可能供其他软件共享的 PawnIO；后者可单独从 Windows 已安装应用中管理。

## 构建

先生成自包含便携目录：

```powershell
./scripts/publish.ps1 -Version 0.4.0
./scripts/build-installer.ps1 -Version 0.4.0 -BootstrapCompiler
```

`-BootstrapCompiler` 固定下载官方 Inno Setup 6.7.4，核对官方 Release SHA-256 与 `Pyrsys B.V.` 签名，再通过官方便携模式仅展开到工作区 `.tools/inno-setup-6.7.4`，不建立系统安装项、快捷方式或文件关联。也可通过 `-CompilerPath C:\path\ISCC.exe` 指定已有官方签名编译器；脚本和预处理检查要求 Inno Setup 6.6.0 或更新版本。

输入为 `artifacts/Roger-win-x64`。脚本检查必需运行时、独立更新组件、许可证文件与实际程序集版本，成功后输出：

- `artifacts/Roger-Setup-0.4.0-x64.exe`
- `artifacts/Roger-Setup-0.4.0-x64.manifest.txt`：Inno 编译文件清单。
- `artifacts/Roger-Setup-0.4.0-x64.sha256`

运行 `./scripts/package.ps1 -Version 0.4.0 -BootstrapCompiler` 可一次生成安装器、`Roger-win-x64.zip` 和包含两者校验值的 `SHA256SUMS.txt`，三者作为 GitHub Release 附件提供。

安装器内保持完整的便携目录依赖和第三方许可证；PawnIO 安装器仅作为临时依赖资源保存。签名和来源见 `third_party/PawnIO`、`third_party/InnoSetup`。若没有配置 Roger 自身的代码签名证书，Roger 和其 Setup 不会伪称具备受信任的发布者签名；被捆绑的官方 PawnIO 签名保持完整。

开发验证包括实际编译、输出 EXE 版本资源、完整文件清单以及纯逻辑/源码检查。构建和检查不会在开发机器上安装 Roger 或重新安装 PawnIO。
