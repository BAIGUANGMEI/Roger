# 优化工具箱

工具箱进入页面时只读取当前状态。更改设置、刷新 DNS 或打开 Windows 工具，均需用户点击对应按钮。应用启动和后台监控不会执行优化操作。

| 工具 | 行为与范围 |
| --- | --- |
| 电源计划 | 使用 Windows Power Management API 列出已有计划并显示当前值。点击“应用计划”后切换，检查 Windows 是否保留所选值。不创建或解锁隐藏计划；原值保存到本地记录，重启 Roger 后仍可选择恢复。高性能计划的效果由设备决定，可能增加耗电和温度。 |
| 客户区域动画 | 使用 SystemParametersInfo 读取或修改客户端动画开关。只影响支持该开关的 Windows 界面，不改动其他视觉效果、透明度或应用自己的动画；原值保存到本地记录，重启后仍可恢复。 |
| DNS 缓存 | 隐藏运行系统 ipconfig.exe /flushdns，输出和退出状态显示在 Roger 内。支持取消，20 秒超时后停止命令。不改变 DNS 服务器或网络适配器设置；后续域名解析会重新建立缓存。 |
| 启动应用 | 打开 Windows 启动应用设置，供用户选择登录时启动的应用。 |
| 存储与存储感知 | 打开 Windows 存储页面，供用户查看空间并配置存储感知。 |
| 游戏模式 / 应用图形首选项 | 打开对应 Windows 设置，由用户按游戏、节能和 GPU 需求选择。可用选项取决于设备及系统版本。 |
| 电源与睡眠 | 打开 Windows 设置以调整睡眠和屏幕关闭时间。 |
| 优化驱动器 / 磁盘清理 | 打开 Windows 自带图形工具，由用户选择磁盘和具体操作。Roger 不会自动整理磁盘或删除文件。 |

系统页面和工具使用固定的白名单入口。当前设备、Windows 版本或组织策略不支持某项功能时，显示实际错误或不可用状态。

## 优化记录与恢复

电源计划和动画的操作记录保存在 `%LOCALAPPDATA%\Roger\optimization-journal.json`，最多保留 200 条。记录包含操作时间、原值、目标值及结果。更改前先原子保存原值及待执行记录，保存失败则不会执行 Windows 设置更改。实际调用设置接口时再次核对预期原值，已发生变化就不写入；不会在写入后才替换记录中的原值。成功后再次检查 Windows 当前值并保存结果；原值和目标值相同的操作只记为“无需更改”。

“恢复上次”使用本地记录中该设置最近可恢复的变更；“恢复选中记录”可以从表格选择。重启软件后这两种恢复方式仍可使用，但启动和刷新记录不会自动更改 Windows 设置。对同一设置应先恢复较新的变更，再恢复更早的变更。恢复前读取当前值：若它与当时应用的目标值不同，且也不是原值，就显示冲突并保留现状；若当前已经是原值，只核对记录而不再次写入。

操作过程中异常退出或操作后记录保存失败时，原来的待执行记录仍在磁盘上，界面显示“待核对”或“结果待核对”。用户可以查看当前状态并显式恢复；系统不会把不确定的结果当作成功。记录文件无法读取时保留原文件；下次显式操作前另存带 `.corrupt-` 后缀的原始副本，再创建有效记录。

DNS 刷新只记录完成、失败或取消结果。清空的 DNS 缓存无法恢复，因此此类记录明确标为不可撤销。通过 Windows 设置和外部系统工具发生的更改，不会伪装成 Roger 已捕获且可恢复的操作。

自动化检查通过模拟 Windows 边界验证计划切换、恢复值、权限失败、DNS 参数、超时、取消和工具白名单，并覆盖记录重载、倒序恢复、外部值冲突、写前/写后保存失败、待核对恢复和损坏文件保留；不会通过测试修改开发者机器的电源、动画或 DNS 设置。

实现依据：

- [Microsoft：PowerEnumerate](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerenumerate)
- [Microsoft：PowerSetActiveScheme](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powersetactivescheme)
- [Microsoft：SystemParametersInfoW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow)
- [Microsoft：Windows 设置 URI](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app)
- [Microsoft：ipconfig](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/ipconfig)
