# DALIZO

DALIZO is a Windows desktop client for Proxmox virtual machines. It combines embedded SPICE, noVNC and serial consoles with VM management.

## Download and start

Download a Windows x64 ZIP from the [Releases page](https://github.com/bernd-spieth/DALIZO/releases), extract the entire ZIP and open `DALIZO.exe`. Do not run the application from inside the ZIP.

Choose the download that suits you:

| Package name ending | .NET installation |
| --- | --- |
| `win-x64.zip` | Included. No separate .NET installation needed. |
| `win-x64-no-runtime.zip` | Smaller download. Install **.NET 10 Desktop Runtime for Windows x64** first. |

The **SDK is not needed to run DALIZO**. The plain .NET Runtime and ASP.NET Core Runtime alone are not sufficient for this desktop application.

See [Prerequisites](PREREQUISITES.md) for official download links and step-by-step installation. SPICE needs VirtViewer x64; noVNC and serial consoles need Microsoft Edge WebView2 Runtime. Neither component is bundled in either ZIP. The executable is not code-signed.

## Connect to Proxmox

1. Open **Options > Manage servers > Add server**.
2. Choose **Automatic** to let DALIZO create its connection using a Proxmox administrator login, or **Manual** to use an existing API token.
3. Enter your server's HTTPS address, for example `https://pve.example.org:8006`.
4. For automatic setup, choose all VMs or enter the specific VM IDs. Review the permissions before creating access. The administrator password is not saved.
5. Open the VM menu and connect to a VM.

The Proxmox HTTPS certificate must be trusted by Windows. Do not publish server profiles or token secrets. Saved secrets are encrypted for the current Windows account. Profiles are stored under `%LOCALAPPDATA%\DalizoCurrent`.

Automatic setup grants console access, status, power controls, CPU, memory, name/description and network settings for the chosen VMs. It grants network use across SDN zones. Existing automatically created connections can use **Manage servers > Repair permissions**. Custom accounts need administrator review.

For manual setup, the chosen VM paths need `VM.Audit`, `VM.Console`, `VM.PowerMgmt`, `VM.Config.CPU`, `VM.Config.Memory`, `VM.Config.Options` and `VM.Config.Network`. All-VM access uses `/vms` with propagation; individual access uses `/vms/<id>`. Network selection requires `SDN.Use` on the desired SDN zones, or `/sdn/zones` with propagation for all zones. Privilege-separated tokens need matching permissions for both the user and token.

## Help and connection errors

Use the **?** next to a field to see its explanation. Hover for a tooltip or click for a help window. Warnings about changes remain visible.

If a dialog fails, expand **Show error details** and use **Copy details**. Errors from the main window are available under **Options > Last error details**. Details include nested causes, so an SSL error can show the underlying certificate or connection error. Known credentials are redacted; review server names and paths before sharing. Certificate validation remains enabled.

## Build from source

Use Windows x64, PowerShell, and the .NET 10 SDK. Internet access is needed to restore NuGet packages. No Proxmox account is needed to build or run the synthetic API checks.

```powershell
.\scripts\build.ps1 -Version 0.1.0-preview.6
```

The script runs the API checks and creates a self-contained Windows x64 ZIP and SHA-256 checksum under `artifacts`. For the smaller `win-x64-no-runtime.zip`, run the same command with `-FrameworkDependent`. Both variants have separate ZIP names and checksums and can be built for the same version.

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
