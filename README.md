# DALIZO

DALIZO is a Windows desktop client for Proxmox virtual machines. It combines embedded SPICE, noVNC and serial consoles with VM management.

## Download and start

Download a Windows x64 ZIP from the [Releases page](https://github.com/bernd-spieth/DALIZO/releases), extract the entire ZIP and open `DALIZO-Native.exe`. Do not run the application from inside the ZIP.

The default release build includes the .NET runtime. These additional components are needed for the respective console types:

- **SPICE:** Install the x64 VirtViewer runtime from the [official download page](https://www.spice-space.org/download.html). DALIZO looks for `VirtViewer*\bin` under Program Files. Alternatively, set `PROXMOX_SPICE_RUNTIME` to a compatible runtime's `bin` directory. The native runtime is not included in the ZIP.
- **noVNC and serial:** Install the [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/).

This is not an all-dependencies installer. The native SPICE libraries and WebView2 Runtime remain separate installations. The executable is not code-signed.

## Connect to Proxmox

1. Open **Options > Manage servers > Add server**.
2. Choose **Automatic** to let DALIZO create its connection using a Proxmox administrator login, or **Manual** to use an existing API token.
3. Enter your server's HTTPS address, for example `https://pve.example.org:8006`.
4. For automatic setup, choose all VMs or enter the specific VM IDs. Review the permissions before creating access. The administrator password is not saved.
5. Open the VM menu and connect to a VM.

The Proxmox HTTPS certificate must be trusted by Windows. Do not publish server profiles or token secrets. Saved secrets are encrypted for the current Windows account. Profiles are stored under `%LOCALAPPDATA%\DalizoCurrent`.

Automatic setup grants console access, status, power controls, CPU, memory, name/description and network settings for the chosen VMs. It grants network use across SDN zones. Existing automatically created connections can use **Manage servers > Repair permissions**. Custom accounts need administrator review.

For manual setup, the chosen VM paths need `VM.Audit`, `VM.Console`, `VM.PowerMgmt`, `VM.Config.CPU`, `VM.Config.Memory`, `VM.Config.Options` and `VM.Config.Network`. All-VM access uses `/vms` with propagation; individual access uses `/vms/<id>`. Network selection requires `SDN.Use` on the desired SDN zones, or `/sdn/zones` with propagation for all zones. Privilege-separated tokens need matching permissions for both the user and token.

## Build from source

Use Windows x64, PowerShell, and the .NET 10 SDK. Internet access is needed to restore NuGet packages. No Proxmox account is needed to build or run the synthetic API checks.

```powershell
.\scripts\build.ps1 -Version 0.1.0-preview.3
```

The script runs the API checks and creates a self-contained Windows x64 ZIP and SHA-256 checksum under `artifacts`. For a smaller build that needs the .NET Desktop Runtime installed separately, add `-FrameworkDependent`.

The project pins the WebView2 SDK package version. Web console libraries are included with their notices. The SPICE runtime is loaded dynamically; native DLLs are not required to compile.

## Tests

```powershell
dotnet run --project tests/ApiChecks.csproj -c Release
```

Additional native engine tests require VirtViewer x64 and an interactive Windows desktop:

```powershell
dotnet run --project engine-tests/EngineTests.csproj -c Release
```

For the focused UI checks, run the built executable with `--smoke-test --vm-menu-test`. This creates synthetic test files next to the executable; do not add those files to a release. These checks do not prove live guest input, clipboard or display performance. Native engine and full rendering tests are not part of the default build.

## Project contents

- `src`: application and embedded web console sources, including focused UI checks.
- `tests`: synthetic Proxmox API and profile checks.
- `engine-tests`: native SPICE engine checks.
- `scripts`: build and package creation.
- `licenses` and `THIRD-PARTY.md`: dependency notices.

Development snapshots, old releases, private profiles, screenshots, live test results and performance captures are intentionally not part of the repository.

## License

DALIZO application code and original assets are licensed under the GNU General Public License, version 3 only (SPDX: GPL-3.0-only). See `LICENSE` and `COPYRIGHT`. Third-party components retain their own licenses; see `THIRD-PARTY.md`. DALIZO is provided without warranty.

The included GitHub Actions workflow builds the Windows package and uploads it as a workflow artifact. It does not publish a GitHub Release automatically.
