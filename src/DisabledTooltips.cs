// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ProxmoxSpice;

public partial class MainWindow
{
    void UpdateDisabledTooltips()
    {
        string consoleReason = vncHost?.IsSerial==true?"Desktop key combinations do not apply to a serial terminal.":!HasDisplayedConsole ? "No console connected and displayed." : busy ? "An operation is in progress. Please wait." : "The console is not ready for keyboard input.";
        void Tip(Control button, string title, string help, string reason)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
            if (!button.IsEnabled) panel.Children.Add(new TextBlock { Text = "Unavailable: " + reason, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, Margin = new Thickness(0,8,0,0) });
            button.ToolTip = new ToolTip { Content = panel, FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };
            ToolTipService.SetShowOnDisabled(button, true);
        }
        Tip(ManagementToggle, "VM actions", "Open VM settings, refresh status and access console actions.", consoleReason);
        Tip(DisconnectButton, "Disconnect", "Close the displayed console. The VM keeps running.", consoleReason);
        bool keysReady=HasDisplayedConsole && (host?.CanSendKeys==true || vncHost is {CanSendKeys:true,IsSerial:false});
        TypeClipboardButton.IsEnabled=keysReady && !busy && typingCancellation==null;
        CancelTypingButton.Visibility=typingCancellation==null?Visibility.Collapsed:Visibility.Visible;
        Tip(TypeClipboardButton,"Type clipboard as keystrokes","Send clipboard characters as keyboard input without a guest agent.",typingCancellation!=null?"Typing is already in progress.":consoleReason);
        CtrlAltDeleteButton.IsEnabled=keysReady;
        if(!keysReady)guestKeysMenu.IsOpen=false;
        Tip(CtrlAltDeleteButton, "Send key combination", "Send Ctrl+Alt+Delete or a Windows/Linux shortcut to the VM.", consoleReason);
        Tip(VmPropertiesButton, "VM settings", "Configure this VM and its console preferences.", consoleReason);
        Tip(VncPasteButton, "Paste clipboard to console", "Use the terminal input or noVNC clipboard channel; this does not simulate keyboard typing.", !ClipboardEnabled ? "Clipboard sharing is disabled in VM settings > Console." : consoleReason);
        RuntimeEntry? entry = null;
        if (targetVm != null) runtime.TryGetValue(targetVm.Id, out entry);
        string reason = targetVm == null ? "No VM selected." : busy ? "An operation is in progress. Please wait." : entry?.Fresh != true ? "The VM status is unavailable or outdated. Wait for a status refresh." : "VM status: " + entry.Label + ".";
        Tip(StartButton, "Start VM", "Power on the selected VM.", reason + (entry?.State?.Status == "running" ? " The VM is already running." : " Start requires a stopped VM."));
        Tip(ShutdownButton, "Shut down VM", "Request a graceful guest shutdown. No forced shutdown fallback.", reason + " Shutdown requires a running guest.");
        Tip(RebootButton, "Restart VM", "Request a guest restart. Pending configuration changes may take effect.", reason + " Restart requires a running guest.");
        Tip(StopButton, "Stop VM (force power off)", "Immediately power off. Unsaved data may be lost; confirmation is required.", reason + " Stop requires a running VM.");
        Tip(RefreshStatusButton, "Refresh status", "Read the current VM state from Proxmox.", reason);
        Tip(OptionsToggle, "Options", "Manage Proxmox servers and global settings.", "An operation is in progress. Please wait.");
    }
}

