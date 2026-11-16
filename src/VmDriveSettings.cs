// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
namespace ProxmoxSpice;

public partial class MainWindow
{
    static void SetDeviceAction(Button button,string caption,bool undo=false){
        button.FontFamily=new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
        button.Content=undo?"\uE7A7":"\uE74D";
        button.FontSize=16;button.Width=32;button.Height=30;button.Padding=new Thickness(0);
        button.ToolTip=caption;
        System.Windows.Automation.AutomationProperties.SetName(button,caption);
    }
    static void PutDeviceAction(GroupBox section,Button button){
        var body=(StackPanel)section.Content;
        var summary=body.Children[0];body.Children.RemoveAt(0);
        var row=new DockPanel();
        DockPanel.SetDock(button,Dock.Right);row.Children.Add(button);row.Children.Add(summary);
        body.Children.Insert(0,row);
    }
    async Task<Dictionary<string,string?>> AddDriveSettings(EditorDialog dialog,ContextMenu menu,
        VmConfiguration original,VmConfiguration active,Vm vm,ProxmoxApi api,bool disk,bool cdrom,bool usb,CancellationToken ct)
    {
        var edits=new Dictionary<string,string?>();
        var keys=original.Values.Keys.Concat(active.Values.Keys)
            .Where(k=>VmConfiguration.IsDrive(k)||VmConfiguration.IsUnused(k)||VmConfiguration.IsUsb(k)).Distinct().Order().ToArray();
        var stores=Array.Empty<string>();
        var isos=Array.Empty<string>();
        dialog.HardwareSection("Disks, CD/DVD and USB devices");
        string? storageError=null;
        if(disk){
            try{
                var writable=new List<string>();
                foreach(var storage in await api.StorageChoicesAsync(vm.Node,"images",ct))
                    if((await api.MissingStoragePrivilegesAsync(storage,ct)).Length==0)writable.Add(storage);
                stores=writable.ToArray();
            }
            catch(System.Net.Http.HttpRequestException){storageError="Could not load disk storage from "+vm.Node+". Check the connection and storage access.";}
        }
        if(cdrom){
            try{isos=await api.StorageChoicesAsync(vm.Node,"iso",ct);}
            catch(System.Net.Http.HttpRequestException){dialog.Hint("Could not load the ISO library from "+vm.Node+".");}
        }
        var diskHint=new TextBlock {TextWrapping=TextWrapping.Wrap};
        dialog.AddField(diskHint);
        var rows=new StackPanel();
        dialog.AddField(rows);
        var entries=new Dictionary<string,MenuItem>();
        string? Free(string bus){
            int count=bus switch{"sata"=>6,"scsi"=>31,"virtio"=>16,"ide"=>4,"usb"=>original.UsbSlots,_=>1};
            return Enumerable.Range(0,count).Select(i=>bus+i).FirstOrDefault(k=>!keys.Contains(k)&&!edits.ContainsKey(k));
        }
        const string optical="CD/DVD Drive",data="Hard Disk",efi="EFI Disk",tpm="TPM State",usbType="USB Device";
        void Render(){
            rows.Children.Clear();
            if(keys.Length==0 && edits.Count==0)rows.Children.Add(new TextBlock {Text="No devices configured."});
            foreach(var key in keys.Concat(edits.Keys).Distinct().Order()){
                var old=original.Get(key);
                bool changed=edits.TryGetValue(key,out var staged);
                string value=changed?staged??old:old;
                bool unused=VmConfiguration.IsUnused(key);
                bool opticalDrive=value.Split(',').Contains("media=cdrom") && !value.Contains(":cloudinit",StringComparison.OrdinalIgnoreCase)
                    && !value.Split(',')[0].Contains("cloudinit",StringComparison.OrdinalIgnoreCase);
                var panel=new StackPanel {Margin=new Thickness(0,5,0,5)};
                rows.Children.Add(panel);
                var details=value==""?active.Get(key):value;
                var parts=details.Split(',');
                var volume=parts[0].Replace("file=","");
                string device=key.StartsWith("efidisk")?"EFI settings disk":key.StartsWith("tpmstate")?"TPM state":
                    VmConfiguration.IsUsb(key)?"USB device":unused?"Unused disk":volume.Contains("cloudinit",StringComparison.OrdinalIgnoreCase)?"Cloud-Init drive":
                    parts.Contains("media=cdrom")?"CD/DVD drive":"Hard disk";
                string description=volume switch {"none"=>"Empty","spice" or "host=spice"=>"SPICE redirection",_=>volume};
                var size=parts.FirstOrDefault(p=>p.StartsWith("size="));
                var row=new DockPanel();
                panel.Children.Add(row);
                var actions=new StackPanel {Orientation=Orientation.Horizontal};
                DockPanel.SetDock(actions,Dock.Right);row.Children.Add(actions);
                row.Children.Add(new TextBlock {Text=$"{device} ({key}) · {description}"+(size==null?"":" · "+size[5..]),ToolTip=details,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center});
                if(changed || old!=active.Get(key))
                    panel.Children.Add(new TextBlock {Text=changed?(staged==null?(unused?"To be permanently deleted":"Removal staged"):"Change staged"):"Pending in Proxmox"});
                void Action(string label,string help,Action click){
                    var button=new Button{Content=label,ToolTip=help,Margin=new Thickness(5,0,0,0)};
                    if(label!="Change ISO")SetDeviceAction(button,help,label=="↶");
                    button.Click+=(_,_)=>click();actions.Children.Add(button);
                }
                if(changed){Action("↶","Undo staged change",()=>{edits.Remove(key);Render();});continue;}
                if(old=="")continue;
                if(VmConfiguration.IsUsb(key)){
                    if(usb && (old.StartsWith("mapping=") || old.Split(',')[0] is "spice" or "host=spice"))
                        Action("×","Remove USB device from this VM",()=>{edits[key]=null;Render();});
                }else if(opticalDrive && cdrom){
                    Action("Change ISO","Select an ISO image or eject the current medium.",()=>Pick(optical,key));
                    Action("×","Remove CD/DVD drive",()=>{edits[key]=null;Render();});
                }else if(disk && !value.Split(',').Contains("media=cdrom")){
                    if(unused)Action("Delete volume…","Permanently delete this unused disk when you select OK in VM settings.",()=>{
                        if(EditorDialog.Confirm(dialog,$"Permanently delete {old} from VM {vm.Id} when you select OK in VM settings?","Delete unused disk")){edits[key]=null;Render();}
                    });
                    else Action("×","Detach this disk when you select OK in VM settings.",()=>{edits[key]=null;Render();});
                }
            }
            entries[optical].IsEnabled=cdrom && Free("ide")!=null;
            string? reason=!disk?$"Missing VM.Config.Disk on /vms/{vm.Id}. For a DALIZO-managed account, open Options > Manage servers > Repair permissions.":
                storageError??(stores.Length==0?$"No writable disk-image storage is available on {vm.Node}. Open Manage servers > Repair permissions and select storage for this node.":
                !new[]{"scsi","sata","virtio"}.Any(b=>Free(b)!=null)?"All supported disk slots are occupied.":null);
            entries[data].IsEnabled=reason==null;
            entries[data].ToolTip=reason??"Add a virtual disk to this VM.";
            ToolTipService.SetShowOnDisabled(entries[data],true);
            diskHint.Text=reason==null?"Add a disk using Add hardware ▾ > Hard Disk.":"Cannot add a hard disk: "+reason;
            entries[efi].IsEnabled=disk && stores.Length>0 && Free("efidisk")!=null;
            entries[tpm].IsEnabled=disk && stores.Length>0 && Free("tpmstate")!=null;
            entries[usbType].IsEnabled=usb && Free("usb")!=null;
        }
        async void Pick(string type,string? existing=null){
            var picker=new EditorDialog(dialog,type,existing==null?"Add":"Use selection");
            string? slot=existing;
            string config="";
            if(type==usbType){
                string[] maps=Array.Empty<string>();
                try{maps=await api.UsbMappingsAsync(vm.Node,ct);}
                catch(OperationCanceledException){return;}
                catch(Exception e){picker.Hint("Host mappings unavailable: "+e.Message);}
                if(!dialog.IsVisible)return;
                const string clientPort="SPICE port (USB on this computer)";
                var mode=picker.Choice("USB source",maps.Select(m=>"Host mapping: "+m).Prepend(clientPort).Cast<object>().ToArray(),clientPort);
                mode.DisplayMemberPath="";
                picker.Help("A SPICE port lets you select a local device from USB devices in the console. A host mapping connects the configured Proxmox device and requires Mapping.Use on that mapping.");
                slot=Free("usb");
                picker.Validate=()=>{string selected=(string)mode.SelectedItem;config=selected==clientPort?"spice":"mapping="+selected["Host mapping: ".Length..];return true;};
            }else if(type==optical){
                const string empty="Empty (eject medium)";
                var prior=existing==null?"none":original.Get(existing).Split(',')[0].Replace("file=","");
                var choices=isos.Append(prior).Where(s=>s!="none").Distinct().Prepend(empty).Cast<object>().ToArray();
                var iso=picker.Choice("ISO image",choices,prior=="none"?empty:prior);iso.DisplayMemberPath="";
                picker.Help("Select an ISO from this node or choose Empty to eject it while keeping the virtual drive.");
                slot??=Free("ide");
                picker.Validate=()=>{
                    var selected=(string)iso.SelectedItem;
                    string options=existing==null?"media=cdrom":string.Join(",",original.Get(existing).Split(',').Skip(1).Where(p=>!p.StartsWith("size=")));
                    config=(selected==empty?"none":selected)+","+options;
                    return true;
                };
            }else{
                var store=picker.Choice("Storage",stores.Cast<object>().ToArray(),stores.FirstOrDefault());store.DisplayMemberPath="";
                picker.Help("Allocate the new volume on the selected Proxmox storage. Allocation requires Datastore.AllocateSpace there.");
                ComboBox? bus=null;TextBox? size=null;
                if(type==data){
                    var buses=new[]{"scsi","sata","virtio"}.Where(b=>Free(b)!=null).Cast<object>().ToArray();
                    bus=picker.Choice("Bus",buses,buses.FirstOrDefault());bus.DisplayMemberPath="";
                    picker.Help("Select the controller for the new disk. DALIZO uses the next free slot on that bus.");
                    size=picker.Text("Size (GiB)","32");picker.Help("Enter the capacity to allocate for the new disk.");
                }else picker.Note(type==efi?"Stores UEFI settings. Adding this disk does not change the VM firmware to OVMF.":"Adds a TPM 2.0 state volume. Detaching it affects guest access to TPM-protected keys.");
                picker.Validate=()=>{
                    int gib=0;
                    if(size!=null && (!int.TryParse(size.Text,out gib)||gib<1))throw new ArgumentException("Enter a positive whole number of GiB.");
                    slot=Free(type==data?(string)bus!.SelectedItem:type==efi?"efidisk":"tpmstate");
                    config=$"{store.SelectedItem}:{(type==data?gib:1)}"+(type==efi?",efitype=4m":type==tpm?",version=v2.0":"");
                    return true;
                };
            }
            if(picker.ShowDialog()==true && slot!=null){edits[slot]=config;Render();}
        }
        foreach(var type in new[]{data,optical,efi,tpm,usbType}){
            var item=new MenuItem {Header=type};
            item.Click+=(_,_)=>Pick(type);
            entries[type]=item;menu.Items.Add(item);
        }
        Render();
        return edits;
    }
}
