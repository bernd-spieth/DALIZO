# DALIZO - server certificate option and startup checks

Add/Edit server now offers **Ignore HTTPS certificate errors (unsafe)**. It is off by default and saved separately for each server. It applies to API requests, automatic administrator setup, permission management and noVNC/serial connections. Changing the address in the editor resets the option. Existing profiles continue to validate certificates.

When enabled, the server identity is not verified and passwords/API tokens could be intercepted. Prefer trusting the correct CA or fixing the server certificate chain. SPICE's ticket certificate handling is unchanged. Reconnect existing consoles after changing the setting.

DALIZO checks the local SPICE library and WebView2 availability at startup. Missing components are reported with download links; server management remains usable. Recheck using **Options > Check prerequisites**. Server certificates and permissions are checked by the server connection test. A missing .NET runtime in the no-runtime package must be installed before DALIZO can start.

Error details now explain PartialChain certificate failures. Short field examples and compact help tooltips are retained.

## Downloads

- **DALIZO-0.1.0-preview.9-win-x64.zip**: includes .NET.
- **DALIZO-0.1.0-preview.9-win-x64-no-runtime.zip**: requires .NET 10 Desktop Runtime for Windows x64, available from [Microsoft](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

Extract the entire ZIP and open DALIZO.exe. See PREREQUISITES.md for installation steps. VirtViewer x64 for SPICE and Microsoft Edge WebView2 Runtime for noVNC/serial remain separate prerequisites. No profiles or credentials are included.

Validation: local loopback TLS tests verified rejection by default, explicit opt-in and isolation; API/profile tests and focused UI tests passed. This does not establish compatibility with every live Proxmox certificate or guest configuration. The executable is unsigned.
