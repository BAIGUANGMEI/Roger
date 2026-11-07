# 物理磁盘监控

Roger 按 Windows 的物理磁盘编号显示磁盘型号、容量、总线、介质类型及盘符映射。一个物理磁盘可以包含多个卷，例如同一块磁盘上的 C: 与 D:。RAID、Storage Spaces、虚拟机和 USB 桥接设备显示的是 Windows 提供程序可见的设备，未必能取得阵列中的每块实际硬盘。

实时活动率、读写速率、队列及平均响应时间来自 `Win32_PerfRawData_PerfDisk_PhysicalDisk`。活动率使用相邻采样的空闲时间计算，读写速率使用字节计数差除以性能时钟间隔。首次采样、计数器重置、设备断开或提供程序不可用时保留空值，不把缺失读数显示为零。没有完成 I/O 的间隔不显示响应时间。

硬件信息来自 `Win32_DiskDrive`、`MSFT_Disk`、`MSFT_PhysicalDisk`，卷映射来自 Windows 的磁盘分区关联。SSD/HDD 根据 Windows 报告的介质类型判断；SCSI 总线不能单独证明设备是 SSD、HDD 或 NVMe。不同 Windows Storage 对象通过唯一 ID 或唯一序列号匹配，不凭相同型号推断。序列号仅用于进程内匹配，不在界面展示。

温度、磨损百分比、通电小时和不可纠正错误数优先读取与磁盘关联的 `MSFT_StorageReliabilityCounter`。如果 LibreHardwareMonitor 提供带有实际物理磁盘编号的温度，则显示该实时温度。温度匹配仅接受 `/nvme/编号`、`/ssd/编号`、`/hdd/编号` 标识；不会把另一块同型号硬盘的温度或警告/临界温度阈值当作当前温度。磨损数值是 Windows 报告的磨损百分比，不等同于剩余寿命预测。

Windows 健康状态会明确标为“Windows 报告”。这不是完整的 SMART 检查，也不能保证硬盘没有故障。部分驱动、控制器或设备不公开可靠性字段；管理员权限也无法补出设备未提供的数据。缺失字段显示“不可用”，不推算温度、寿命或健康分数。

磁盘运行状态按 `MSFT_Disk.OperationalStatus` 的官方值解释，兼容提供程序返回的单值及数组。Windows 扩展状态 `0xD010` 至 `0xD014` 分别显示在线、未就绪、无介质、离线、故障。未知的厂商状态保留原始数值；“未活动”和“已停止”分别显示，不推断设备的电源状态。

实时计数每次采样只进行一次 Windows 查询。成功取得的硬件和可靠性信息缓存 60 秒；设备清单为空，或设备、卷、介质清单查询出现错误时，查询结束后至少等待 5 秒再重试。设备未提供的可靠性字段仍按 60 秒周期检查，避免反复查询不受支持的数据。WMI 连接和枚举均设置 2 秒超时。所有操作均只读，不启用计数器、不执行磁盘扫描或修复、不安装驱动。

实现依据：

- [Microsoft：物理磁盘原始性能计数器](https://learn.microsoft.com/en-us/previous-versions/aa394308(v=vs.85))
- [Microsoft：计数器计算公式](https://learn.microsoft.com/en-us/windows/win32/wmisdk/counter-algorithm-counter-types)
- [Microsoft：磁盘可靠性计数器](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-storagereliabilitycounter)
- [Microsoft：Windows 磁盘与运行状态值](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk)
- [Microsoft：磁盘与可靠性计数器的关联](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disktostoragereliabilitycounter)
- [LibreHardwareMonitor：存储设备物理编号与温度传感器](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/Hardware/Storage/StorageDevice.cs)
