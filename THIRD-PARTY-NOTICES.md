# Third-party software

Kelvra is built on these projects. Thank you to their authors.

| Component | Used for | License |
|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | Reading all hardware sensors (temperatures, clocks, loads, fans, SMART) | [MPL-2.0](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LICENSE) |
| [PawnIO](https://pawnio.eu) by namazso | Kernel driver LibreHardwareMonitor uses for CPU temperatures/voltages. The official, digitally signed `PawnIO_setup.exe` is downloaded from [its releases](https://github.com/namazso/PawnIO.Setup/releases) at build time, hash-checked and embedded unmodified; Kelvra only runs it when the user agrees. | See [pawnio.eu](https://pawnio.eu) and [github.com/namazso/PawnIO](https://github.com/namazso/PawnIO) |
| [Intel PresentMon](https://github.com/GameTechDev/PresentMon) | Measuring FPS and frame times in games. The official, Intel-signed `PresentMon-x64.exe` console app is downloaded from [its releases](https://github.com/GameTechDev/PresentMon/releases) at build time, hash-checked and embedded unmodified; Kelvra runs it in the background while *Settings → Gaming → Measure FPS* is on. | [MIT](https://github.com/GameTechDev/PresentMon/blob/main/LICENSE.txt) |
| [.NET](https://github.com/dotnet/runtime) / WPF / Windows Forms | Application framework | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |

LibreHardwareMonitorLib pulls in further packages (for example HidSharp, System.Management,
DiskInfoToolkit, RAMSPDToolkit). Their licenses are listed on their NuGet pages and are
included in the respective packages.

Icons in the user interface come from the Segoe Fluent Icons / Segoe MDL2 Assets fonts that ship with Windows.

## Intel PresentMon license

Kelvra.exe contains an unmodified copy of PresentMon, distributed under this license:

```
Copyright (C) 2017-2024 Intel Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom
the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included
in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL
THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE
OR OTHER DEALINGS IN THE SOFTWARE.
```
