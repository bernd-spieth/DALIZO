# Prerequisites

DALIZO is a Windows x64 desktop application. Extract the complete download before opening `DALIZO.exe`.

## What do I need to install?

Choose the package and console types you use. If the matching component is already installed, you do not need to install it again.

| Component | When needed | Included in DALIZO? |
| --- | --- | --- |
| .NET 10 Desktop Runtime, Windows x64 | Only for the ZIP ending in `no-runtime.zip` | Included in the standard ZIP; omitted from `no-runtime.zip` |
| VirtViewer x64 / compatible SPICE runtime | SPICE consoles | No |
| Microsoft Edge WebView2 Evergreen Runtime | noVNC and serial consoles | No |
| .NET 10 SDK | Only to build DALIZO from source | Not required for users |

## Install .NET for the smaller package

1. Open the [official Microsoft .NET 10 download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
2. Find **Run apps - Runtime**, then **.NET Desktop Runtime**.
3. Under **Windows**, choose the **x64 installer** for the latest available 10.0 patch.
4. Run the downloaded installer and follow Microsoft's instructions.
5. Open `DALIZO.exe` from the extracted DALIZO folder.

Choose **Desktop Runtime**, not the plain .NET Runtime or ASP.NET Core Runtime. The Desktop Runtime includes the base .NET Runtime, so no second .NET installer is needed. You do not need the SDK, Visual Studio or a developer toolkit. Installing another major version alone does not replace the required .NET 10 runtime.

DALIZO does not download or install .NET silently. The user runs the Microsoft installer. If you do not want a separate .NET installation, use the standard DALIZO ZIP instead.

For an optional installation check, run this in a terminal:

```powershell
& "$env:ProgramFiles\dotnet\dotnet.exe" --list-runtimes
```

Look for `Microsoft.WindowsDesktop.App 10.0.x` in the x64 installation. If it is missing, install the Desktop Runtime as described above.

## SPICE consoles

Install **VirtViewer x64** using the Windows installer linked from the [official SPICE download page](https://www.spice-space.org/download.html). DALIZO looks for a `VirtViewer*\bin` folder under Program Files. Advanced users can set `PROXMOX_SPICE_RUNTIME` to the `bin` directory of an interface-compatible runtime.

This component runs on your Windows computer. SPICE guest tools, which provide guest features such as clipboard sharing and resolution changes, are installed separately inside the VM when needed.

## noVNC and serial consoles

Use the [official Microsoft WebView2 download page](https://developer.microsoft.com/en-us/microsoft-edge/webview2/). Choose the **Evergreen Runtime** (x64 standalone installer, or Microsoft's bootstrapper). If that runtime is already installed, keep using it.

## Proxmox connection

You need a reachable Proxmox server with a trusted HTTPS certificate and an API token with permission to use the selected VMs. DALIZO can create its access using **Options > Manage servers > Add server > Automatic**. This requires a Proxmox administrator login; the administrator password is not saved.

The selected console must also be configured in Proxmox. A serial console needs a serial device and a guest operating system configured to use it.

## Microsoft reference

Microsoft explains the distinction between runtime and SDK, and which runtime runs WPF and Windows Forms applications, in [Install .NET on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows).

## Startup check and certificate errors

DALIZO checks the local SPICE library and WebView2 availability when it starts. Missing components are reported with download links; server management remains usable. Run the check again from **Options > Check prerequisites**. If .NET is missing from a no-runtime installation, Windows must first start the .NET host: install the Desktop Runtime or use the package that includes it.

HTTPS certificates are checked when connecting to each server, not by the local component check. `PartialChain` means Windows could not build the certificate chain to a root CA. A missing CA/intermediate certificate or an incomplete server chain can cause this. This is separate from VirtViewer and WebView2 installation.

**Add server** and **Edit server** offer **Ignore HTTPS certificate errors (unsafe)**. It is off by default, saved per server, and reset in the editor if you change the address. It applies to API requests (including administrator setup and permission management) and noVNC/serial WebSockets. The server identity is no longer verified; passwords and API tokens could be intercepted. Prefer trusting the correct CA or fixing the server certificate chain. SPICE's own ticket certificate handling is unchanged. Reconnect existing consoles after changing this setting.

If a manual connection test, automatic administrator login or user-initiated console connection fails certificate verification, DALIZO offers **Keep verification** (the default) or **Ignore and retry**. The prompt shows the server and certificate error. Accepting retries once and enables the exception for that profile. Unsaved profiles must still be saved; for an existing console connection the setting is saved immediately. An inventory certificate failure is available under Options > Last error details > Review certificate failure, without interrupting background refreshes. Login failures and other network errors do not trigger the prompt. Re-enable checking in Edit server.
