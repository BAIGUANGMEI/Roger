# Third-party notices

The MIT license in this repository covers Roger's own application code. It does not replace any dependency's license. Roger uses unmodified NuGet dependencies as separate assemblies; published distributions keep those assemblies replaceable and include these notices.

## LibreHardwareMonitorLib 0.9.6

- Project and source: [LibreHardwareMonitor v0.9.6](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6).
- Exact release source archive: [v0.9.6 source](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/archive/refs/tags/v0.9.6.zip).
- Package: [LibreHardwareMonitorLib 0.9.6](https://www.nuget.org/packages/LibreHardwareMonitorLib/0.9.6).
- Main license: Mozilla Public License 2.0, included in [third_party/LibreHardwareMonitor/LICENSE](third_party/LibreHardwareMonitor/LICENSE).
- Copyright belongs to the LibreHardwareMonitor/OpenHardwareMonitor contributors as identified in the upstream source files.
- Upstream components include additional terms, including PawnIO.Modules under LGPL 2.1. The upstream release's complete notice file is retained at [third_party/LibreHardwareMonitor/THIRD-PARTY-NOTICES.txt](third_party/LibreHardwareMonitor/THIRD-PARTY-NOTICES.txt). Corresponding component sources and notices are available through the pinned upstream source tree and its submodules/references.

Roger does not modify LibreHardwareMonitorLib. Its source remains available at the source links above under the original license. If modifying or redistributing that dependency, preserve its notices and make its corresponding modified source available under the applicable terms. Roger's application sources and build scripts are distributed separately in this repository.

## Microsoft .NET and System.Management

The Windows desktop application uses .NET 10 / WPF and the System.Management NuGet package. Microsoft .NET libraries are supplied under their original license terms, generally MIT. A self-contained publish includes runtime license and third-party notice files where supplied by the .NET SDK; preserve them.

- [.NET runtime source and license](https://github.com/dotnet/runtime).
- [WPF source and license](https://github.com/dotnet/wpf).
- [System.Management package](https://www.nuget.org/packages/System.Management).

## Additional hardware libraries

The resolved hardware support libraries below are unmodified. For each MPL-2.0 library, corresponding source is available at the pinned source URL. The MPL-2.0 text is included in `third_party/LibreHardwareMonitor/LICENSE`.

| Package | Copyright / license | Corresponding source |
| --- | --- | --- |
| DiskInfoToolkit 1.1.2 | Florian K. / MPL-2.0 | [25319eae](https://github.com/Blacktempel/DiskInfoToolkit/tree/25319eae5781e75bcf141e844ceab2afe94d40ea) |
| RAMSPDToolkit-NDD 1.4.2 | Florian K. / MPL-2.0 | [3b47b960](https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce) |
| BlackSharp.Core 1.0.7 | Florian K. / MPL-2.0 | [c70b735c](https://github.com/Blacktempel/BlackSharp/tree/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0) |
| HidSharp 2.6.4 | Copyright 2010–2025 James Bellinger / see included license | [Project](https://software.seekye.com/hidsharp), [included license](third_party/HidSharp/LICENSE.txt) |

## Transitive dependencies

NuGet also resolves dependencies of LibreHardwareMonitorLib, including hardware access/support libraries and Microsoft framework libraries. Those packages retain their own licenses. See the pinned upstream [project dependencies](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/LibreHardwareMonitorLib.csproj) and the resolved `Roger.deps.json` supplied with each build. The publish script writes `DEPENDENCIES.txt` containing the resolved package list; that inventory is an aid to inspecting the exact release, not a replacement for upstream licenses.

No Cloudflare speed-test software is bundled. The optional user-initiated network test calls Cloudflare's public HTTPS service.

## Lucide icons

Navigation and window caption icons use unmodified SVG originals from [Lucide](https://github.com/lucide-icons/lucide/tree/53fab88ff9d1b48f4704b5b5b560d6b4d560bf4c/icons), pinned to commit `53fab88ff9d1b48f4704b5b5b560d6b4d560bf4c`, converted to WPF geometries. Original SVGs are retained in `src/Roger/Assets/Icons`; the full ISC and inherited Feather MIT terms are included in [third_party/Lucide/LICENSE](third_party/Lucide/LICENSE). Roger's R monogram is original application artwork.

## Official PawnIO prerequisite

Roger's Windows installer bundles the unmodified [official signed PawnIO 2.2.0 installer](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0) and installs it automatically only when a compatible installed version is missing. Its original binary-distribution notice permits redistributing that installer unmodified; the notice and exact source/hash information are retained in [third_party/PawnIO](third_party/PawnIO). Packaging verifies SHA-256 and Windows Authenticode; setup checks the bundled installer hash again before running the normal signed edition. Roger does not distribute a patched or separately extracted driver and does not use unrestricted mode. Uninstalling Roger leaves the shared PawnIO installation intact.

The portable ZIP does not install system drivers merely by being extracted; Roger's hardware-access page can download the same verified official prerequisite when requested. The open-source driver project has distinct GPL source terms and an interface exception; those are not a replacement for the actual official signed installer's included distribution notice.

## Inno Setup installer runtime and language messages

The Windows installer is generated using [Inno Setup 6.7.4](https://github.com/jrsoftware/issrc/releases/tag/is-6_7_4) and its upstream Simplified Chinese language messages. The original license and source attribution are retained in [third_party/InnoSetup](third_party/InnoSetup). The compiler is only a build tool; the generated Setup executable contains the installer runtime under its original license. Roger does not relicense those components.
