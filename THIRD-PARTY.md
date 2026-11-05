# Third-party components

Keep the bundled license files and per-file copyright headers when redistributing DALIZO.

| Component | Version | License / location |
| --- | --- | --- |
| noVNC | 1.6.0 | MPL-2.0 and bundled component terms; `src/Web/LICENSE.txt`, `src/Web/docs`, `src/Web/AUTHORS` |
| pako | bundled with noVNC | MIT; `src/Web/vendor/pako/LICENSE` and file headers |
| xterm.js | 5.5.0 | MIT; `src/Web/terminal/LICENSE-xterm.txt` |
| xterm addon-fit | 0.10.0 | MIT; `src/Web/terminal/LICENSE-fit.txt` |
| Microsoft.Web.WebView2 SDK | 1.0.4314.35 | `licenses/WebView2-LICENSE.txt` and `licenses/WebView2-NOTICE.txt` |

In a compiled package, `src/Web` is distributed as `Web`. The corresponding JavaScript source and license notices are included. noVNC's DES implementation has additional BSD-style notices in its source headers. The OFL document is retained from upstream; it does not mean this package bundles the Orbitron font.

Source projects:
- https://github.com/novnc/noVNC/tree/v1.6.0
- https://github.com/xtermjs/xterm.js
- https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4314.35

## Separately installed native SPICE runtime

DALIZO dynamically loads the spice-client-glib library and its GLib/GObject dependencies from a separately installed compatible VirtViewer runtime. DALIZO supplies its own Windows display and input integration; it does not embed the GTK display widget.

The downloaded DALIZO ZIP does not contain VirtViewer, SPICE, GTK or their native DLL dependencies. Obtain the runtime from https://www.spice-space.org/download.html and retain the terms supplied by its distributor. `licenses/LGPL-2.1.txt` and `licenses/LGPL-2.0.txt` are included as reference copies; they are not a complete license inventory for a separate VirtViewer installer.

An interface-compatible replacement runtime can be selected using `PROXMOX_SPICE_RUNTIME`. This distribution imposes no additional restriction on modification or reverse engineering for debugging changes to LGPL libraries. Do not add native runtime binaries to a release without reviewing their exact licenses and corresponding-source obligations.

## Microsoft runtimes

The WebView2 Runtime is installed separately and is not included. Self-contained builds include the .NET runtime and its package-supplied license/notice files; preserve those files in the release ZIP. The SDK license copies above do not replace the runtime notices.
