# DALIZO - certificate retry and VM menu test fix

If certificate verification fails during a manual connection test, administrator login or console connection, DALIZO shows the server and the reason. **Keep verification** is the default. **Ignore and retry** explicitly enables the exception for this server and retries once. With the exception enabled, passwords and API tokens could be intercepted. Other connection errors do not trigger this prompt.

The VM sorting test now renders the real VM list independently of popup focus. It checks the sort arrow, selection, search and saved order with 1,000 synthetic VMs. The focused VM menu suite passed twice with the fix; the sorting behavior of the application itself is unchanged.

## Downloads

- **DALIZO-0.1.0-preview.10-win-x64.zip**: includes .NET.
- **DALIZO-0.1.0-preview.10-win-x64-no-runtime.zip**: requires .NET 10 Desktop Runtime for Windows x64, available from [Microsoft](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

Extract the entire ZIP and open DALIZO.exe. See PREREQUISITES.md for installation steps. VirtViewer x64 for SPICE and Microsoft Edge WebView2 Runtime for noVNC/serial remain separate prerequisites. No profiles or credentials are included.

Validation: local loopback TLS tests verified rejection by default, explicit opt-in and isolation; API/profile tests and focused UI tests passed. This does not establish compatibility with every live Proxmox certificate or guest configuration. The executable is unsigned.
