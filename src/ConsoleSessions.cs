// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;

namespace ProxmoxSpice;

public partial class MainWindow
{
    sealed record ConsoleSession(Guid VmId, SpiceHost? Spice, NoVncHost? Vnc)
    {
        public FrameworkElement View => (FrameworkElement?)Spice ?? Vnc!;
        public bool Open => Spice?.SessionOpen ?? Vnc?.SessionOpen == true;
        public void Dispose() { Spice?.Dispose(); Vnc?.Dispose(); }
    }
    readonly Dictionary<Guid, ConsoleSession> consoles = new();
    readonly Grid consoleDeck = new();

    void RememberConsole(SavedVm vm)
    {
        var session = new ConsoleSession(vm.Id, host, vncHost);
        consoles[vm.Id] = session; TouchConsole(vm.Id);
        ConsoleArea.Child = null;
        consoleDeck.Children.Add(session.View);
        ConsoleArea.Child = consoleDeck;
        RefreshConsoleSessions();
    }
    void ParkConsole()
    {
        host?.PauseInteraction(); vncHost?.PauseInteraction();
        if(host!=null && !consoles.Values.Any(s=>s.Spice==host))host.Dispose();
        if(vncHost!=null && !consoles.Values.Any(s=>s.Vnc==vncHost))vncHost.Dispose();
        foreach(var session in consoles.Values) session.View.Visibility = Visibility.Hidden;
        host = null; vncHost = null;
        UpdateConnectButtons();
        CtrlAltDeleteButton.IsEnabled = VncPasteButton.IsEnabled = false;
        VncPasteButton.Visibility = Visibility.Collapsed;
    }
    bool RestoreConsole(SavedVm vm)
    {
        if (!consoles.TryGetValue(vm.Id, out var session)) return false;
        TouchConsole(vm.Id);
        ConsoleArea.Child = consoleDeck;
        host = session.Spice; vncHost = session.Vnc;
        session.View.Visibility = Visibility.Visible;
        host?.SetExtraSharing(targetVm?.ImageClipboardEnabled==true,targetVm?.FileTransferEnabled==true);
        host?.SetClipboardSharing(ClipboardEnabled);
        host?.SetResizeEnabled(AutoResolutionEnabled);
        vncHost?.SetResizeEnabled(AutoResolutionEnabled);
        vncHost?.SetClipboardSharing(ClipboardEnabled);

        VncPasteButton.Visibility = vncHost != null ? Visibility.Visible : Visibility.Collapsed;
        CtrlAltDeleteButton.IsEnabled = host?.CanSendKeys == true || vncHost is {CanSendKeys:true,IsSerial:false};
        VncPasteButton.IsEnabled = vncHost?.CanSendKeys == true && ClipboardEnabled;
        Status(session.Open ? "Returned to the existing console session." : "This console has disconnected. Use Connect to reconnect.");
        RefreshConsoleSessions();
        return true;
    }
    void CloseConsole(Guid id)
    {
        if (!consoles.Remove(id, out var session)) return;
        recentConsoles.Remove(id);
        if (host == session.Spice && host != null) host = null;
        if (vncHost == session.Vnc && vncHost != null) vncHost = null;
        session.Dispose(); consoleDeck.Children.Remove(session.View);
        if (targetVm?.Id == id) {
            if(!closing)SetConsoleFocus(false);
            CtrlAltDeleteButton.IsEnabled = VncPasteButton.IsEnabled = false;
            VncPasteButton.Visibility = Visibility.Collapsed;
            ConsoleArea.Child = new TextBlock { Text="Console closed. Click Connect to reconnect.", Margin=new Thickness(24) };
        }
        RefreshConsoleSessions();
    }
    void CloseAllConsoles()
    {
        foreach(var id in consoles.Keys.ToArray()) CloseConsole(id);
    }
    void UpdateSessionIndicators()
    {
        if(VmList==null)return;
        foreach(var row in VmList.Items.OfType<VmRow>())
            row.SetSessionState(consoles.TryGetValue(row.Id,out var session)
                ? session.Open ? "Console open" : "Console disconnected" : "");
    }
    void RefreshConsoleSessions()
    {
        UpdateSessionIndicators();
        UpdateConnectButtons();
        var orphan=consoles.Keys.FirstOrDefault(id=>!data.Vms.Any(v=>v.Id==id));
        if(orphan!=Guid.Empty)CloseConsole(orphan);
    }
    void UpdateConnectButtons()
    {
        UpdateFeatureIndicators();
        if(VmPropertiesButton!=null)VmPropertiesButton.IsEnabled=HasDisplayedConsole && !busy;
        if(ManagementToggle!=null) {
            ManagementToggle.IsEnabled=HasDisplayedConsole && !busy;
            if(!ManagementToggle.IsEnabled)ManagementToggle.IsChecked=false;
        }
        if(DisconnectButton!=null) {
            DisconnectButton.IsEnabled=HasDisplayedConsole;
            DisconnectHelp.Text=HasDisplayedConsole ? "Other open console sessions remain connected." : "No active console displayed.";
        }
        UpdateDisabledTooltips();UpdateFileTransferButton();
        if(VmList!=null)foreach(var row in VmList.Items.OfType<VmRow>()) {
            UpdateRowPower(row);
            bool hasSession=consoles.ContainsKey(row.Id);
            row.SetActions(!closing && !busy, !closing && !busy && hasSession);
        }
    }
    bool HasDisplayedConsole => !closing && targetVm!=null
        && consoles.TryGetValue(targetVm.Id,out var displayed) && displayed.Open
        && displayed.View.Visibility==Visibility.Visible && ConsoleArea.Child==consoleDeck
        && ((host!=null && displayed.Spice==host) || (vncHost!=null && displayed.Vnc==vncHost));
}






