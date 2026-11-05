# DALIZO - choice of runtime packages

Two Windows x64 downloads are available:

- **DALIZO-0.1.0-preview.5-win-x64.zip**: includes .NET. No separate .NET installation needed.
- **DALIZO-0.1.0-preview.5-win-x64-no-runtime.zip**: smaller download. Install the **.NET 10 Desktop Runtime for Windows x64** from [Microsoft](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) first. The SDK is not needed.

Extract the complete ZIP and start `DALIZO.exe`.

Both packages include `PREREQUISITES.md` with installation steps and official download links. VirtViewer x64 for SPICE and Microsoft Edge WebView2 Runtime for noVNC/serial remain separate prerequisites. No runtime is silently downloaded or installed.

The executable is unsigned. This preview has synthetic API and focused UI validation; it has not been validated on a clean Windows installation with every guest configuration.
