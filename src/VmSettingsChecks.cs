// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;

namespace ProxmoxSpice;
public partial class MainWindow
{
    async Task CheckVmSettings(SavedVm vm,bool hardwareAllowed=false)
    {
        var factory = apiFactory; var snapshot = Snapshot(); int writes = 0;
        Exception? failure = null; bool inspected = false;
        IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject {
            for (int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) {
                var child = VisualTreeHelper.GetChild(root,i);
                if(child is T match)yield return match;
                foreach(var match2 in Find<T>(child))yield return match2;
            }
        }
        apiFactory = (server, secret) => new ProxmoxApi(server.Address, new SmokeHttp(req => {
            if(req.Method != System.Net.Http.HttpMethod.Get)writes++;
            return req.RequestUri!.AbsolutePath.EndsWith("/config")
                ? "{\"data\":{\"name\":\"Guest\",\"audio0\":\"device=ich9-intel-hda,driver=spice\",\"cores\":1,\"sockets\":1,\"memory\":512,\"net0\":\"virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr-old,tag=42\"}}"
                : req.RequestUri.AbsolutePath.Contains("resources")
                ? "{\"data\":[{\"type\":\"qemu\",\"vmid\":101,\"node\":\"node\",\"name\":\"Guest\",\"status\":\"stopped\"}]}"
                : req.RequestUri.AbsolutePath.EndsWith("/network") ? "{\"data\":[{\"iface\":\"vmbr0\",\"type\":\"bridge\"},{\"iface\":\"vmbr1\",\"type\":\"OVSBridge\"}]}" : "{\"data\":{\"/vms/101\":{\"VM.Config.Network\":1"+(hardwareAllowed?",\"VM.Config.HWType\":1":"")+"}}}";
        }));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_,_) => {
            var dialog = Application.Current.Windows.OfType<EditorDialog>().FirstOrDefault(w=>w.Title.StartsWith("VM settings ·"));
            if(dialog == null)return;
            timer.Stop();
            try {
                dialog.UpdateLayout();
                var tabs = Find<TabControl>(dialog).Single();
                if(!tabs.Items.Cast<TabItem>().Select(t=>t.Header).SequenceEqual(new[]{"General","Hardware","Console","VM options","Advanced"}))throw new Exception("Unified settings tabs missing");
                var optionDialog=new EditorDialog(dialog,"Option checks");
                var sample=new VmConfiguration("",new(){{"hotplug","1"},{"boot","order=scsi0;net0"},{"scsi0","pool:disk"},{"net0","virtio"},{"startup","order=2,up=15"}});
                var collect=AddVmOptions(optionDialog,sample,true,true,true,true,true);
                optionDialog.Show();optionDialog.UpdateLayout();
                try{
                    var edits=new Dictionary<string,string>();var deletions=new List<string>();
                    collect(edits,deletions);
                    if(edits.Count!=0 || deletions.Count!=0)throw new Exception("Unchanged VM options generated edits.");
                    Find<CheckBox>(optionDialog).Single(b=>Equals(b.Content,"Processors")).IsChecked=true;
                    Find<CheckBox>(optionDialog).Single(b=>Equals(b.Content,"Start at host boot")).IsChecked=true;
                    var up=Find<Button>(optionDialog).Single(b=>Equals(b.ToolTip,"Move net0 up"));
                    up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    optionDialog.UpdateLayout();
                    if(Find<Button>(optionDialog).Single(b=>Equals(b.ToolTip,"Move net0 up")).IsEnabled)
                        throw new Exception("First boot device can still move up.");
                    collect(edits,deletions);
                    if(edits.Count!=3 || edits["boot"]!="order=net0;scsi0" || edits["onboot"]!="1" || !edits["hotplug"].Split(',').Contains("cpu"))
                        throw new Exception("Boot order, autostart or hotplug changes incorrect.");
                }finally{optionDialog.Close();}
                tabs.SelectedIndex=1;dialog.UpdateLayout();
                var add=Find<Button>(dialog).Single(b=>b.Content as string=="Add hardware ▾");
                if(!add.IsEnabled || !add.ContextMenu.Items.OfType<MenuItem>().Single(m=>m.Header as string=="Network adapter").IsEnabled || Find<GroupBox>(dialog).Any(g=>g.IsVisible && g.Header.ToString()!.StartsWith("Serial port")))
                    throw new Exception("Hardware access or empty slot visibility incorrect.");
                Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{
                    var picker=Application.Current.Windows.OfType<EditorDialog>().Single(w=>w.Owner==dialog);
                    picker.UpdateLayout();
                    Find<ComboBox>(picker).Single(b=>b.IsEditable).Text="vmbr1";
                    Find<Button>(picker).Single(b=>b.Content as string=="Add").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }));
                add.ContextMenu.Items.OfType<MenuItem>().Single(m=>m.Header as string=="Network adapter").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                dialog.UpdateLayout();
                Find<Button>(dialog).Single(b=>b.ToolTip as string=="Cancel addition of net1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(hardwareAllowed){
                    var remove=Find<Button>(dialog).Single(b=>b.ToolTip as string=="Remove Audio device (audio0)");
                    if(remove.Content as string!="\uE74D")throw new Exception("Audio removal icon missing.");
                    remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if(remove.Content as string!="\uE7A7")throw new Exception("Undo removal icon missing.");
                    remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var serial=add.ContextMenu.Items.OfType<MenuItem>().Single(m=>m.Header as string=="Serial port (socket)");
                    for(int i=0;i<4;i++)serial.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    dialog.UpdateLayout();
                    if(serial.IsEnabled)throw new Exception("Full serial slots allow another addition.");
                    var cancel=Find<Button>(dialog).Where(b=>b.IsVisible && b.Content as string=="\uE7A7" && b.ToolTip is string hint && hint.StartsWith("Cancel addition of serial")).ToArray();
                    if(cancel.Length!=4)throw new Exception("Serial sockets were not staged.");
                    foreach(var b in cancel)b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if(!serial.IsEnabled)throw new Exception("Cancelled slots unavailable.");
                }
                tabs.SelectedIndex=1;dialog.UpdateLayout();
                var bridge=Find<ComboBox>(dialog).Single(b=>b.IsEditable);
                if(bridge.Text!="vmbr-old"||!bridge.Items.Cast<string>().Contains("vmbr0")||!bridge.Items.Cast<string>().Contains("vmbr1"))throw new Exception("Bridge choices or current assignment lost.");
                if(!bridge.IsEnabled)throw new Exception("Authorized bridge selection is disabled.");
                var networkImage=new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32);networkImage.Render(dialog);var networkPng=new PngBitmapEncoder();networkPng.Frames.Add(BitmapFrame.Create(networkImage));using(var file=File.Create(Path.Combine(AppContext.BaseDirectory,"network-bridges-preview.png")))networkPng.Save(file);
                var fallbackDialog=new EditorDialog(this,"Synthetic fallback");var fallback=AddBridgeChoice(fallbackDialog,"net0","vmbr-existing",Array.Empty<string>());
                if(fallback.Text!="vmbr-existing"||!fallback.IsEditable)throw new Exception("Missing bridge list discarded assignment or manual fallback.");
                tabs.SelectedIndex=2;dialog.UpdateLayout();
                Find<CheckBox>(dialog).Single(c=>c.Content as string=="Share clipboard with this VM").IsChecked=false;
                Find<CheckBox>(dialog).Single(c=>c.Content as string=="Share images through SPICE clipboard (PNG)").IsChecked=true;
                Find<CheckBox>(dialog).Single(c=>c.Content as string=="Allow sending files to this VM through SPICE").IsChecked=true;
                var audio = Find<CheckBox>(dialog).Single(c=>c.Content as string=="Play VM audio through SPICE");
                var video = Find<CheckBox>(dialog).Single(c=>c.Content as string=="Prefer Motion JPEG for SPICE video");
                if(audio.IsChecked!=true || video.IsChecked!=true)throw new Exception("Unexpected SPICE media defaults.");
                audio.IsChecked=false;
                video.IsChecked=false;
                var image = new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32);
                image.Render(dialog);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                using(var file=File.Create(Path.Combine(AppContext.BaseDirectory,"vm-settings-tabs.png")))encoder.Save(file);
                inspected=true;
                if(hardwareAllowed){tabs.SelectedIndex=1;dialog.UpdateLayout();bridge.Text="vmbr1";dialog.DialogResult=false;}
                else Find<Button>(dialog).Single(b=>b.Content as string=="OK").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            } catch(Exception e) { failure=e;dialog.Close(); }
        };
        try {
            timer.Start();await ConfigureVm(vm);
            if(failure!=null)throw failure;
            var savedMedia = store.Load().Vms.Single(v=>v.Id==vm.Id);
            if(hardwareAllowed){
                if(!inspected || writes!=0 || savedMedia!=snapshot.Vms.Single(v=>v.Id==vm.Id))throw new Exception("Cancelling sent hardware changes or modified the local profile.");
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"hardware-cancel-result.txt"),"PASS: hardware and console edits discarded, no Proxmox writes. Synthetic VM.");return;
            }
            if(savedMedia.SpiceAudioEnabled || savedMedia.PreferSpiceMjpeg)throw new Exception("SPICE media preferences were not persisted.");
            if(!inspected || data.Vms.Single(v=>v.Id==vm.Id).ClipboardEnabled || !data.Vms.Single(v=>v.Id==vm.Id).ImageClipboardEnabled || !data.Vms.Single(v=>v.Id==vm.Id).FileTransferEnabled || writes!=0)
                throw new Exception("Local settings did not save independently of Proxmox configuration");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"vm-settings-result.txt"),"PASS: all five tabs, boot reorder, host autostart, hotplug, unchanged options produce no edits, local save sends no API writes. Synthetic VM.");
        } finally {timer.Stop();apiFactory=factory;Commit(snapshot,vm.Id);}
    }
}
