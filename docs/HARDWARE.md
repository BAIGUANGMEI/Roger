# 硬件数据来源与支持范围

Roger 的监控数值来自本机 Windows API 和 LibreHardwareMonitor，不使用演示数据。初始化和采样应在后台串行执行；硬件清单首次通过 WMI 查询后缓存，不在每次刷新时重新查询。

| 指标 | 来源与含义 |
| --- | --- |
| CPU 使用率 | 优先读取 LibreHardwareMonitor 的 CPU Total；不可用时通过 GetSystemTimes 两次采样的系统空闲、内核、用户时间差计算。第一笔有效结果需要建立基线。 |
| CPU 温度 | 适配 Intel / AMD 已知传感器名称，优先读取封装、Tdie 或 Tctl/Tdie；缺少该传感器时取有效核心 / CCD 温度的最高值。忽略温差上限等非实际温度读数，并将未初始化的 0°C 视为不可用。多 CPU 设备分别显示；概览取有效 CPU 温度的最高值。 |
| GPU | NVIDIA / AMD / Intel 设备分别保留稳定标识，可手动选择概览和监控条使用的显卡，选择保存到配置。自动模式依次优先已识别的独显、未知类型、集显，同级考虑报告容量和遥测可用性，不按厂商品牌排序。手选设备暂时消失时保留选择并回退到可用设备。 |
| GPU 使用率 | 来自 GPU Core 负载传感器，或可用的 3D 引擎传感器。驱动未提供时显示不可用。 |
| GPU 内存 | 读取同一设备的 GPU Memory Used/Total 或完整的 D3D Dedicated Memory 数据，单位为 MiB，不混用不同统计域的已用量与总量。D3D Shared Memory 单独展示，不用共享内存补齐专用内存。集显也可能报告专用分配，此字段不表示设备必然有独立显存芯片。不使用可能在大于 4 GiB 显卡上溢出的 WMI AdapterRAM 字段。 |
| 内存 | GlobalMemoryStatusEx 返回的物理内存已用量及总量，单位为 GiB。不是各进程工作集之和。 |
| 内存条 | Win32_PhysicalMemory 返回厂商、型号、插槽、容量、配置速率和固件报告速率，品牌不做过滤。不把未知 JEDEC 厂商代码猜测为具体品牌，不把固件速率当成 SPD / XMP / EXPO 实测结果。 |
| 网络 | 所有已连接的物理以太网和 Wi-Fi 网卡的收发字节差除以实际采样间隔，单位为 B/s。优先使用 NDIS 的 MSFT_NetAdapter 物理接口 GUID 匹配网卡，每 60 秒更新设备清单，连接状态逐次读取。排除 WFP、QoS、Native Wi-Fi 等隐藏过滤接口，避免重复计数。该提供程序不可用时退回 Win32_NetworkAdapter 与名称过滤，并提示兼容模式。多个活动物理网卡显示“合计”；纯 VPN、虚拟机或特殊网卡环境可能无法显示流量。此数值代表当前流量，不是带宽测速结果。 |
| 磁盘空间 | DriveInfo 返回的本地固定盘和已就绪可移动盘总容量及总可用空间，每 10 秒更新。网络映射盘不参与此处容量监控。 |
| 物理硬盘 | Windows 物理磁盘性能计数器与 Storage 提供程序返回型号、介质、活动率、读写速率及可用可靠性指标，按物理设备关联卷与温度。见 [物理磁盘说明](STORAGE.md)。 |
| 其他传感器 | LibreHardwareMonitor 实际报告的主板、风扇、温度、电压、功率、存储等传感器。种类和完整程度取决于硬件、固件、驱动与权限。 |
| 硬件清单 | WMI 的 CPU、显卡、内存条、主板、BIOS、磁盘、操作系统和电池信息，以及 Windows 网卡信息。查询失败的类别明确显示“信息不可用”。 |

## 权限与可用性

Roger.exe 通过 Windows 应用清单默认请求管理员权限，确认 UAC 后启动。LibreHardwareMonitor 0.9.6 的底层 CPU 温度、主板及风扇访问使用 **PawnIO 驱动**；仅提升权限但没有安装驱动，仍可能无法读取温度。

多品牌适配使用真实设备标识与传感器名称；设备清单的 WMI 回退会保留尚无可用遥测的设备。支持某一厂商不表示该厂商所有型号、固件或驱动组合均可读取全部字段。内存条品牌和速率来自 SMBIOS，不能替代内存稳定性测试。

Windows 安装器包含 [PawnIO 官方签名原包 2.2.0](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0)，安装时自动检测版本，在缺失或版本较旧时安装，依赖无需另外下载。已有兼容版本时跳过，不降级、不自动重启。构建时核验固定 SHA-256 与 Windows Authenticode，安装前再次校验包内容。

便携版的硬件页仍提供“启用 CPU 温度”入口和管理员模式切换。用户点击后下载同一固定版本，验证 SHA-256 与签名，再由 Windows 请求管理员授权。应用日常监控不会自行安装驱动。安装后重启 Roger 到管理员模式，重新初始化传感器。

应用只使用官方签名版，不选择安装器的 unrestricted 选项，不修改 Windows 安全策略，也不要求关闭防护。部分设备或固件仍可能不提供可用传感器；缺失的数据明确显示“不可用”。CPU、RAM、网络与可读磁盘仍可继续监控。显式开启登录自启时，使用当前用户的最高权限交互式登录计划任务启动到托盘，不保存密码。

驱动访问状态会区分未安装驱动、需要管理员模式、驱动尚未就绪及硬件没有有效读数。安装器启动之后会等待其完成，不在安装过程中强行中断；安装后仍需重启时会显示状态说明。

监控软件只能报告硬件公开的状态。WMI 磁盘 `Status=OK` 不是完整 SMART 检测，也不保证磁盘没有故障。初版不包含坏道检测、压力测试、风扇控制或自动硬件修复。文件空间扫描和文件系统检查的使用方式见项目 README。

## 单位

- `GiB` = 1024³ 字节，`MiB` = 1024² 字节；网络测速使用十进制 `Mbps`。
- 网络监控统计网卡报告的字节计数，不以图表瞬时点推断运营商实际可用带宽。
- 多网卡、桥接、硬件卸载、采样时机及不同传感器定义会造成与任务管理器等软件的读数差异。

## 参考

- [LibreHardwareMonitor 官方项目及权限说明](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
- [LibreHardwareMonitor 0.9.6 官方安装驱动代码](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitor/UI/MainForm.cs)
- [PawnIO 官方签名版与源码说明](https://pawnio.eu/)
- [Windows GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)
- [Windows GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex)
- [Windows Win32_VideoController](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-videocontroller)
- [Windows Win32_PhysicalMemory](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-physicalmemory)
