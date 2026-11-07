# 硬件数据来源与初版范围

Roger 的监控数值来自本机 Windows API 和 LibreHardwareMonitor，不使用演示数据。初始化和采样应在后台串行执行；硬件清单首次通过 WMI 查询后缓存，不在每次刷新时重新查询。

| 指标 | 来源与含义 |
| --- | --- |
| CPU 使用率 | 优先读取 LibreHardwareMonitor 的 CPU Total；不可用时通过 GetSystemTimes 两次采样的系统空闲、内核、用户时间差计算。第一笔有效结果需要建立基线。 |
| CPU 温度 | 优先读取封装或 Tctl/Tdie 温度；缺少该传感器时读取已检测到的 CPU 温度中的最高值。并非所有处理器都开放这些数据。部分不支持的 CPU/GPU 传感器会返回未初始化的 0°C，初版将此值视为不可用。 |
| GPU | 概览固定选择一张显卡，优先 NVIDIA，其次 AMD 独显或 Intel Arc，再选择集显；同级按名称排序。AMD 集显通过型号名称识别。概览中的负载、温度、显存都来自该显卡；完整清单和传感器列表保留其他显卡。初版尚无概览显卡选择器。 |
| GPU 使用率 | 来自 GPU Core 负载传感器，或可用的 3D 引擎传感器。驱动未提供时显示不可用。 |
| 显存 | 读取显卡的 GPU Memory Used/Total，或 D3D Dedicated Memory 传感器，单位为 MiB。不会使用可能在大于 4 GiB 显卡上溢出的 WMI AdapterRAM 字段。 |
| 内存 | GlobalMemoryStatusEx 返回的物理内存已用量及总量，单位为 GiB。不是各进程工作集之和。 |
| 网络 | 所有已连接的物理以太网和 Wi-Fi 网卡的收发字节差除以实际采样间隔，单位为 B/s。优先使用 NDIS 的 MSFT_NetAdapter 物理接口 GUID 匹配网卡，每 60 秒更新设备清单，连接状态逐次读取。排除 WFP、QoS、Native Wi-Fi 等隐藏过滤接口，避免重复计数。该提供程序不可用时退回 Win32_NetworkAdapter 与名称过滤，并提示兼容模式。多个活动物理网卡显示“合计”；纯 VPN、虚拟机或特殊网卡环境可能无法显示流量。此数值代表当前流量，不是带宽测速结果。 |
| 磁盘空间 | DriveInfo 返回的本地固定盘和已就绪可移动盘总容量及总可用空间，每 10 秒更新。网络映射盘不参与此处容量监控。 |
| 其他传感器 | LibreHardwareMonitor 实际报告的主板、风扇、温度、电压、功率、存储等传感器。种类和完整程度取决于硬件、固件、驱动与权限。 |
| 硬件清单 | WMI 的 CPU、显卡、内存条、主板、BIOS、磁盘、操作系统和电池信息，以及 Windows 网卡信息。查询失败的类别明确显示“信息不可用”。 |

## 权限与可用性

普通用户权限下即可运行。部分低层温度、主板、风扇传感器需要硬件驱动及管理员权限；Windows 安全策略也可能阻止驱动访问。应用不会自动提升权限、修改安全策略或提示关闭 Windows 防护。缺失的温度和 GPU 指标显示不可用，不用 0 代替；CPU、RAM、网络与可读磁盘仍可继续监控。

监控软件只能报告硬件公开的状态。WMI 磁盘 `Status=OK` 不是完整 SMART 检测，也不保证磁盘没有故障。初版不包含坏道检测、压力测试、风扇控制或自动硬件修复。文件空间扫描和文件系统检查的使用方式见项目 README。

## 单位

- `GiB` = 1024³ 字节，`MiB` = 1024² 字节；网络测速使用十进制 `Mbps`。
- 网络监控统计网卡报告的字节计数，不以图表瞬时点推断运营商实际可用带宽。
- 多网卡、桥接、硬件卸载、采样时机及不同传感器定义会造成与任务管理器等软件的读数差异。

## 参考

- [LibreHardwareMonitor 官方项目及权限说明](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
- [Windows GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)
- [Windows GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex)
- [Windows Win32_VideoController](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-videocontroller)
