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
    async Task CheckVmSettings(SavedVm vm)
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
                ? "{\"data\":{\"name\":\"Guest\",\"cores\":1,\"sockets\":1,\"memory\":512,\"net0\":\"virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr-old,tag=42\"}}"
                : req.RequestUri.AbsolutePath.Contains("resources")
                ? "{\"data\":[{\"type\":\"qemu\",\"vmid\":101,\"node\":\"node\",\"name\":\"Guest\",\"status\":\"stopped\"}]}"
                : req.RequestUri.AbsolutePath.EndsWith("/network") ? "{\"data\":[{\"iface\":\"vmbr0\",\"type\":\"bridge\"},{\"iface\":\"vmbr1\",\"type\":\"OVSBridge\"}]}" : "{\"data\":{\"/vms/101\":{\"VM.Config.Network\":1}}}";
        }));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_,_) => {
            var dialog = Application.Current.Windows.OfType<EditorDialog>().FirstOrDefault(w=>w.Title.StartsWith("VM settings ·"));
            if(dialog == null)return;
            timer.Stop();
            try {
                dialog.UpdateLayout();
                var tabs = Find<TabControl>(dialog).Single();
                if(!tabs.Items.Cast<TabItem>().Select(t=>t.Header).SequenceEqual(new[]{"General","Network","Console"}))throw new Exception("Unified settings tabs missing");
                tabs.SelectedIndex=1;dialog.UpdateLayout();
                var bridge=Find<ComboBox>(dialog).Single();
                if(bridge.Text!="vmbr-old"||!bridge.Items.Cast<string>().Contains("vmbr0")||!bridge.Items.Cast<string>().Contains("vmbr1"))throw new Exception("Bridge choices or current assignment lost.");
                if(!bridge.IsEnabled)throw new Exception("Authorized bridge selection is disabled.");
                var networkImage=new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32);networkImage.Render(dialog);var networkPng=new PngBitmapEncoder();networkPng.Frames.Add(BitmapFrame.Create(networkImage));using(var file=File.Create(Path.Combine(AppContext.BaseDirectory,"network-bridges-preview.png")))networkPng.Save(file);
                var fallbackDialog=new EditorDialog(this,"Synthetic fallback");var fallback=AddBridgeChoice(fallbackDialog,"net0","vmbr-existing",Array.Empty<string>());
                if(fallback.Text!="vmbr-existing"||!fallback.IsEditable)throw new Exception("Missing bridge list discarded assignment or manual fallback.");
                tabs.SelectedIndex=2;dialog.UpdateLayout();
                Find<CheckBox>(dialog).Single(c=>c.Content as string=="Share clipboard with this VM").IsChecked=false;
                Find<CheckBox>(dialog).Single(c=>c.Content as string=="Share images through SPICE clipboard (PNG)").IsChecked=true;
                Find<CheckBox>(dialog).Single(c=>c.Content as string=="Allow sending files to this VM through SPICE").IsChecked=true;
                var image = new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32);
                image.Render(dialog);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                using(var file=File.Create(Path.Combine(AppContext.BaseDirectory,"vm-settings-tabs.png")))encoder.Save(file);
                inspected=true;
                Find<Button>(dialog).Single(b=>b.Content as string=="Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            } catch(Exception e) { failure=e;dialog.Close(); }
        };
        try {
            timer.Start();await ConfigureVm(vm);
            if(failure!=null)throw failure;
            if(!inspected || data.Vms.Single(v=>v.Id==vm.Id).ClipboardEnabled || !data.Vms.Single(v=>v.Id==vm.Id).ImageClipboardEnabled || !data.Vms.Single(v=>v.Id==vm.Id).FileTransferEnabled || writes!=0)
                throw new Exception("Local settings did not save independently of Proxmox configuration");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"vm-settings-result.txt"),"PASS: General/Network/Console tabs; enabled bridge choices; missing current bridge retained; empty-list editable fallback; local save sends no API writes.");
        } finally {timer.Stop();apiFactory=factory;Commit(snapshot,vm.Id);}
    }
}
