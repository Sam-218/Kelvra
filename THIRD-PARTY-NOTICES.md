# Third-party software

Kelvra is built on these projects. Thank you to their authors.

| Component | Used for | License |
|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | Reading all hardware sensors (temperatures, clocks, loads, fans, SMART) | [MPL-2.0](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LICENSE) |
| [PawnIO](https://pawnio.eu) by namazso | Kernel driver LibreHardwareMonitor uses for CPU temperatures/voltages. The official, digitally signed `PawnIO_setup.exe` is downloaded from [its releases](https://github.com/namazso/PawnIO.Setup/releases) at build time, hash-checked and embedded unmodified; Kelvra only runs it when the user agrees. | See [pawnio.eu](https://pawnio.eu) and [github.com/namazso/PawnIO](https://github.com/namazso/PawnIO) |
| [.NET](https://github.com/dotnet/runtime) / WPF / Windows Forms | Application framework | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |

LibreHardwareMonitorLib pulls in further packages (for example HidSharp, System.Management,
DiskInfoToolkit, RAMSPDToolkit). Their licenses are listed on their NuGet pages and are
included in the respective packages.

Icons in the user interface come from the Segoe Fluent Icons / Segoe MDL2 Assets fonts that ship with Windows.
