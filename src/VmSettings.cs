// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProxmoxSpice;

public partial class MainWindow
{
    Func<SavedVm> AddConsoleSettings(EditorDialog dialog, SavedVm vm, Action? addServerStreaming=null)
    {
        dialog.Tab("Console");
        var name = dialog.Text("Custom display name", vm.Name);
        dialog.Help("Console preferences are saved locally in DALIZO. Choose the name shown for this VM in the DALIZO list. Edit the server-side name on the General tab.",false);
        var folder=dialog.Choice("Folder",data.Vms.Select(v=>v.Folder).Append("").Distinct().Order().Cast<object>().ToArray(),vm.Folder);
        folder.DisplayMemberPath="";folder.IsEditable=true;folder.Text=vm.Folder;
        dialog.Help("Select an existing folder or type a new folder name. Leave this field blank to show the VM under Ungrouped.",false);
        var console = dialog.Choice("Console", new object[] { "Auto", "SPICE", "noVNC", "Serial terminal" }, vm.ConsoleType);
        console.DisplayMemberPath = "";
        dialog.Help("Auto selects SPICE for QXL displays, otherwise noVNC. Changing the console type closes its current session.",false);
        var serial=dialog.Choice("Serial port",new object[]{"serial0","serial1","serial2","serial3"},vm.SerialPort);serial.DisplayMemberPath="";
        serial.IsEnabled=vm.ConsoleType=="Serial terminal";
        dialog.Help("Serial terminal requires a socket-backed serial device in Proxmox and a login/getty in the guest. DALIZO does not change guest boot or login settings.",false);
        var audio = dialog.Check("Play VM audio through SPICE", vm.SpiceAudioEnabled);
        dialog.Help("Hear sound from the VM on this computer. The VM needs an audio device with the SPICE backend. To add one, open Hardware > Add hardware > Audio device. Reconnect the SPICE console after changing this option.",false);
        var mjpeg = dialog.Check("Prefer Motion JPEG for SPICE video", vm.PreferSpiceMjpeg);
        dialog.Help("Ask the SPICE server to use Motion JPEG for video in the VM. The server must support this preference. Clear the checkbox for automatic codec selection. Reconnect the SPICE console to apply the change.",false);
        addServerStreaming?.Invoke();
        var clipboard = dialog.Check("Share clipboard with this VM", vm.ClipboardEnabled);
        var clipboardHelp=dialog.Help("Share text between this computer and the guest. For SPICE, install the SPICE guest agent in the VM.",false);
        var imageClipboard=dialog.Check("Share images through SPICE clipboard (PNG)",vm.ImageClipboardEnabled);
        dialog.Help("Copy and paste PNG images between this computer and the VM over SPICE. Image sharing can be enabled independently of text sharing and requires the SPICE guest agent.",false);
        var fileTransfer=dialog.Check("Allow sending files to this VM through SPICE",vm.FileTransferEnabled);
        dialog.Help("Send individual files from this computer to the VM over SPICE. Install the SPICE guest agent in the VM. Select files rather than folders.",false);
        var resolution = dialog.Check("Auto resolution", vm.AutoResolution);
        var resolutionHelp=dialog.Help("Adjust the guest display resolution when the console window changes size. SPICE uses the guest SPICE agent and compatible display drivers. noVNC requests guest resizing when supported by the server and scales the image as a fallback.",false);
        var clipboardIntro=clipboardHelp.Text;
        var resolutionIntro=resolutionHelp.Text;
        void RefreshSupport()
        {
            serial.IsEnabled=(string?)console.SelectedItem=="Serial terminal";
            audio.IsEnabled = mjpeg.IsEnabled = console.SelectedItem is "Auto" or "SPICE";
            consoles.TryGetValue(vm.Id, out var session);
            bool changed = (string?)console.SelectedItem != vm.ConsoleType;
            bool connected = !changed && session?.Open == true;
            var spice = session?.Spice;
            bool agent = connected && spice?.GuestAgentConnected == true;
            void Set(TextBlock text,string intro,bool enabled,bool resize)
            {
                if(serial.IsEnabled){text.Text=intro+"\n\n"+(resize?"Fits terminal rows and columns locally. Guest applications may need stty rows/cols; a serial line does not convey guest display resolution.":"Copy selected terminal text and paste input using Ctrl+Shift+C/V or the context menu. No guest clipboard agent required.");return;}
                var feature = DescribeFeature(connected, spice != null, enabled, agent,
                    agent && (resize ? spice!.MonitorCapability : spice!.ClipboardCapability), resize);
                text.Text = intro+"\n\n"+feature.State + " — " + (changed
                    ? "Apply the console type change and reconnect to check support."
                    : feature.Help);
            }
            Set(clipboardHelp,clipboardIntro, clipboard.IsChecked == true, false);
            Set(resolutionHelp,resolutionIntro, resolution.IsChecked == true, true);
        }
        clipboard.Checked += (_, _) => RefreshSupport();
        clipboard.Unchecked += (_, _) => RefreshSupport();
        resolution.Checked += (_, _) => RefreshSupport();
        resolution.Unchecked += (_, _) => RefreshSupport();
        console.SelectionChanged += (_, _) => RefreshSupport();
        ConsoleFeaturesChanged += RefreshSupport;
        dialog.Closed += (_, _) => ConsoleFeaturesChanged -= RefreshSupport;
        RefreshSupport();

        return () => {
            var updated = vm with { Name = name.Text.Trim(), Folder = folder.Text.Trim(), ConsoleType = (string)console.SelectedItem, ExplicitConsole = true,
                SpiceAudioEnabled = audio.IsChecked == true, PreferSpiceMjpeg = mjpeg.IsChecked == true,
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
        var dialog = new EditorDialog(this, "VM settings · " + vm.Name, "OK");
        var read = AddConsoleSettings(dialog, vm);
        dialog.Tab("General");
        dialog.Note("Proxmox configuration could not be loaded. Check the server connection and token permissions. Local console preferences remain available on the Console tab.");
        dialog.Tab("Hardware");
        dialog.Note("Hardware configuration is unavailable until Proxmox configuration can be loaded.");
        SavedVm? updated = null;
        dialog.Validate = () => { updated = read(); return true; };
        if (dialog.ShowDialog() == true && updated != null) { SaveConsoleSettings(vm, updated); Status("Console preferences saved locally; Proxmox configuration was not changed."); }
    }
}

