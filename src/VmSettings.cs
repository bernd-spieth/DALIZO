// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProxmoxSpice;

public partial class MainWindow
{
    Func<SavedVm> AddConsoleSettings(EditorDialog dialog, SavedVm vm)
    {
        dialog.Tab("Console");
        dialog.Note($"VM {vm.VmId} · These preferences are saved locally in DALIZO.");
        var name = dialog.Text("Custom display name", vm.Name);
        var folder=dialog.Choice("Folder (local; blank = Ungrouped)",data.Vms.Select(v=>v.Folder).Append("").Distinct().Order().Cast<object>().ToArray(),vm.Folder);
        folder.DisplayMemberPath="";folder.IsEditable=true;folder.Text=vm.Folder;
        var console = dialog.Choice("Console", new object[] { "Auto", "SPICE", "noVNC", "Serial terminal" }, vm.ConsoleType);
        console.DisplayMemberPath = "";
        dialog.Help("Auto selects SPICE for QXL displays, otherwise noVNC. Changing the console type closes its current session.");
        var serial=dialog.Choice("Serial port",new object[]{"serial0","serial1","serial2","serial3"},vm.SerialPort);serial.DisplayMemberPath="";
        serial.IsEnabled=vm.ConsoleType=="Serial terminal";
        dialog.Help("Serial terminal requires a socket-backed serial device in Proxmox and a login/getty in the guest. DALIZO does not change guest boot or login settings.");
        var clipboard = dialog.Check("Share clipboard with this VM", vm.ClipboardEnabled);
        var imageClipboard=dialog.Check("Share images through SPICE clipboard (PNG)",vm.ImageClipboardEnabled);
        var fileTransfer=dialog.Check("Allow sending files to this VM through SPICE",vm.FileTransferEnabled);
        dialog.Help("Images and file sending require a SPICE connection and guest-agent support. File sending is host to VM only; folders are not supported.");
        var clipboardInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray };
        dialog.AddField(clipboardInfo);
        var resolution = dialog.Check("Auto resolution", vm.AutoResolution);
        var resolutionInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray };
        dialog.AddField(resolutionInfo);
        void RefreshSupport()
        {
            serial.IsEnabled=(string?)console.SelectedItem=="Serial terminal";
            consoles.TryGetValue(vm.Id, out var session);
            bool changed = (string?)console.SelectedItem != vm.ConsoleType;
            bool connected = !changed && session?.Open == true;
            var spice = session?.Spice;
            bool agent = connected && spice?.GuestAgentConnected == true;
            void Set(TextBlock text, bool enabled, bool resize)
            {
                if(serial.IsEnabled){text.Text=resize?"Fits terminal rows and columns locally. Guest applications may need stty rows/cols; a serial line does not convey guest display resolution.":"Copy selected terminal text and paste input using Ctrl+Shift+C/V or the context menu. No guest clipboard agent required.";return;}
                var feature = DescribeFeature(connected, spice != null, enabled, agent,
                    agent && (resize ? spice!.MonitorCapability : spice!.ClipboardCapability), resize);
                text.Text = feature.State + " — " + (changed
                    ? "Apply the console type change and reconnect to check support."
                    : feature.Help);
            }
            Set(clipboardInfo, clipboard.IsChecked == true, false);
            Set(resolutionInfo, resolution.IsChecked == true, true);
        }
        clipboard.Checked += (_, _) => RefreshSupport();
        clipboard.Unchecked += (_, _) => RefreshSupport();
        resolution.Checked += (_, _) => RefreshSupport();
        resolution.Unchecked += (_, _) => RefreshSupport();
        console.SelectionChanged += (_, _) => RefreshSupport();
        ConsoleFeaturesChanged += RefreshSupport;
        dialog.Closed += (_, _) => ConsoleFeaturesChanged -= RefreshSupport;
        RefreshSupport();
        dialog.Help("Clipboard and automatic resolution require guest support. SPICE uses the guest SPICE agent and compatible display drivers. noVNC requests guest resizing when supported by the server and scales the image as a fallback.");
        return () => {
            var updated = vm with { Name = name.Text.Trim(), Folder = folder.Text.Trim(), ConsoleType = (string)console.SelectedItem, ExplicitConsole = true,
                ClipboardEnabled = clipboard.IsChecked == true, ImageClipboardEnabled=imageClipboard.IsChecked==true, FileTransferEnabled=fileTransfer.IsChecked==true, AutoResolution = resolution.IsChecked == true, SerialPort=(string)serial.SelectedItem };
            var next = Snapshot(); next.Vms.RemoveAll(v => v.Id == vm.Id); next.Vms.Add(updated);
            ProfileStore.Validate(next);
            return updated;
        };
    }
    void SaveConsoleSettings(SavedVm original, SavedVm updated)
    {
        if (!data.Vms.Any(v => v.Id == original.Id)) return;
        if (original.ConsoleType != updated.ConsoleType || original.SerialPort != updated.SerialPort) CloseConsole(original.Id);
        var next = Snapshot(); next.Vms.RemoveAll(v => v.Id == original.Id); next.Vms.Add(updated);
        Commit(next, updated.Id);
        if (targetVm?.Id == updated.Id) { targetVm = updated; ApplyConsolePreferences(); UpdateRuntimeHeader(); }
    }
    void ShowLocalVmSettings(SavedVm vm)
    {
        var dialog = new EditorDialog(this, "VM settings · " + vm.Name);
        var read = AddConsoleSettings(dialog, vm);
        dialog.Tab("General");
        dialog.Note("Proxmox configuration could not be loaded. Check the server connection and token permissions. Local console preferences remain available on the Console tab.");
        dialog.Tab("Network");
        dialog.Note("Network configuration is unavailable until Proxmox configuration can be loaded.");
        SavedVm? updated = null;
        dialog.Validate = () => { updated = read(); return true; };
        if (dialog.ShowDialog() == true && updated != null) { SaveConsoleSettings(vm, updated); Status("Console preferences saved locally; Proxmox configuration was not changed."); }
    }
}

