// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Media;
namespace ProxmoxSpice;
public partial class MainWindow
{
    static string StatusSymbolFor(string label) => label switch
    {
        "Running" => "▶", "Stopped" or "Guest shut down" => "■", "Paused" or "Not running" => "Ⅱ",
        "Stale status" => "◷", "Unreachable" or "Not visible" or "Token required" => "⚠", _ => "?"
    };
    static Brush StatusBrushFor(string label) => label switch
    {
        "Running" => Brushes.ForestGreen, "Stopped" or "Guest shut down" => Brushes.SlateGray,
        "Paused" or "Not running" or "Stale status" => Brushes.DarkGoldenrod,
        "Unreachable" or "Not visible" or "Token required" => Brushes.Firebrick, _ => Brushes.SlateGray
    };
    bool ClipboardEnabled => targetVm?.ClipboardEnabled ?? true;
    bool AutoResolutionEnabled => targetVm?.AutoResolution ?? true;
    void ApplyConsolePreferences()
    {
        bool enabled=ClipboardEnabled;
        host?.SetResizeEnabled(AutoResolutionEnabled);
        vncHost?.SetResizeEnabled(AutoResolutionEnabled);
        host?.SetExtraSharing(targetVm?.ImageClipboardEnabled==true,targetVm?.FileTransferEnabled==true);
        host?.SetClipboardSharing(enabled); vncHost?.SetClipboardSharing(enabled);
        if(VncPasteButton!=null)VncPasteButton.IsEnabled=enabled && vncHost?.CanSendKeys==true;
    }
}



