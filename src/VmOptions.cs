// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
namespace ProxmoxSpice;

public partial class MainWindow
{
    static Action<Dictionary<string,string>,List<string>> AddVmOptions(EditorDialog dialog,VmConfiguration cfg,
        bool options,bool hardware,bool disk,bool power,bool startup){
        var readers=new List<Action<Dictionary<string,string>,List<string>>>();
        void Flag(string key,string label,bool fallback,bool editable,string help){
            bool initial=cfg.Number(key,fallback?1:0)!=0;
            var box=dialog.Check(label,initial);box.IsEnabled=editable;dialog.Help(help);
            readers.Add((c,_)=>{if(editable && box.IsChecked!=initial)c[key]=box.IsChecked==true?"1":"0";});
        }
        void Choice(string key,string label,string[] values,string fallback,bool editable,string help){
            string initial=cfg.Get(key,fallback);
            var box=dialog.Choice(label,values.Append(initial).Distinct().Cast<object>().ToArray(),initial);
            box.DisplayMemberPath="";box.IsEnabled=editable;dialog.Help(help);
            readers.Add((c,del)=>{
                string selected=(string)box.SelectedItem;
                if(!editable || selected==initial)return;
                if(selected=="Default")del.Add(key);else c[key]=selected;
            });
        }
        static string[] Parts(string value)=>value.Split(',',StringSplitOptions.RemoveEmptyEntries);
        static string Part(string raw,string key,string fallback="")=>Parts(raw).FirstOrDefault(v=>v.StartsWith(key+"="))?[(key.Length+1)..]??fallback;
        dialog.Tab("VM options");
        Flag("onboot","Start at host boot",false,options,"Start this VM when the Proxmox node boots.");
        Flag("protection","Protect VM and disks",false,options,"Protect this VM against deletion and disk removal in Proxmox.");
        Choice("ostype","Guest OS type",new[]{"other","l26","l24","win11","win10","win8","win7","w2k8","wxp","solaris"},"other",options,"Choose the guest OS family used by Proxmox for VM defaults.");
        Flag("tablet","Match the VM pointer to the mouse position",true,hardware,"The VM sees a virtual tablet that places its pointer where you click in the console.");
        string rawStart=cfg.Get("startup");
        var startParts=new Dictionary<string,TextBox>();
        foreach(var (key,label) in new[]{("order","Host startup order"),("up","Startup delay (seconds)"),("down","Shutdown timeout (seconds)")}){
            var box=dialog.Text(label,Part(rawStart,key));box.IsEnabled=startup;
            dialog.Help(key=="order"?"Lower numbers start first. Leave blank for any order.":"Enter a whole number of seconds or leave blank for the host default.");
            startParts[key]=box;
        }
        if(!startup)dialog.Hint("Changing host startup order or delays requires VM.Config.Options and Sys.Modify on /.");
        readers.Add((c,del)=>{
            if(!startup || startParts.All(p=>p.Value.Text.Trim()==Part(rawStart,p.Key)))return;
            var parts=Parts(rawStart).Where(p=>!startParts.ContainsKey(p.Split('=')[0])).ToList();
            foreach(var (key,box) in startParts){
                var value=box.Text.Trim();if(value=="")continue;
                if(!int.TryParse(value,out var n)||n<0)throw new ArgumentException("Startup order and delays must be non-negative whole numbers.");
                parts.Add(key+"="+n);
            }
            if(parts.Count==0)del.Add("startup");else c["startup"]=string.Join(",",parts);
        });
        string boot=cfg.Get("boot");
        var enabled=Part(boot,"order").Split(';',StringSplitOptions.RemoveEmptyEntries);
        var order=new StackPanel {IsEnabled=disk};
        var devices=new List<CheckBox>();
        dialog.AddField(new TextBlock {Text="Boot order"});dialog.AddField(order);
        dialog.Hint(disk?"The VM tries checked devices from top to bottom. Use the arrows to change their order.":"Boot order is read-only. This connection needs VM.Config.Disk on the VM. For a DALIZO-managed account, use Manage servers > Repair permissions.");
        foreach(var key in enabled.Concat(cfg.Values.Keys.Where(k=>VmNetwork.IsKey(k) || VmConfiguration.IsDrive(k) && k is not ("efidisk0" or "tpmstate0")).Order()).Distinct()){
            var device=VmNetwork.IsKey(key)?"Network adapter":cfg.Get(key).Split(',').Contains("media=cdrom")?"CD/DVD drive":"Disk";
            devices.Add(new CheckBox {Tag=key,Content=device+" ("+key+")",ToolTip=cfg.Get(key),IsChecked=enabled.Contains(key),VerticalAlignment=VerticalAlignment.Center});
        }
        void RenderOrder(){
            foreach(var row in order.Children.Cast<DockPanel>())row.Children.Clear();
            order.Children.Clear();
            foreach(var device in devices){
                var row=new DockPanel();
                var arrows=new StackPanel {Orientation=Orientation.Horizontal};
                DockPanel.SetDock(arrows,Dock.Right);row.Children.Add(arrows);row.Children.Add(device);order.Children.Add(row);
                foreach(int step in new[]{-1,1}){
                    string label=$"Move {device.Tag} "+(step<0?"up":"down");
                    int target=devices.IndexOf(device)+step;
                    var button=new Button {Content=step<0?"↑":"↓",ToolTip=label,Width=36,IsEnabled=target>=0 && target<devices.Count};
                    System.Windows.Automation.AutomationProperties.SetName(button,label);
                    arrows.Children.Add(button);
                    button.Click+=(_,_)=>{devices.Remove(device);devices.Insert(target,device);RenderOrder();};
                }
            }
        }
        RenderOrder();
        if(boot!="" && !boot.StartsWith("order="))dialog.Hint("Current legacy boot setting: "+boot+". Selecting devices replaces it with an explicit order.");
        readers.Add((c,_)=>{
            var selected=devices.Where(b=>b.IsChecked==true).Select(b=>(string)b.Tag).ToArray();
            if(!disk || enabled.SequenceEqual(selected))return;
            if(selected.Length==0)throw new ArgumentException("Select at least one boot device.");
            c["boot"]="order="+string.Join(";",selected);
        });
        string hot=cfg.Get("hotplug","1");
        var current=Parts(hot=="1"?"disk,network,usb":hot=="0"?"":hot);
        var features=new Dictionary<string,CheckBox>();
        dialog.Note("Which devices may change while the VM is running?");
        foreach(var key in new[]{"disk","network","usb","cpu","memory","cloudinit"}){
            var label=key switch {"disk"=>"Disks","network"=>"Network adapters","usb"=>"USB devices","cpu"=>"Processors","memory"=>"Memory",_=>"Cloud-Init drive"};
            var box=dialog.Check(label,current.Contains(key));box.IsEnabled=hardware;
            dialog.Help("Allow Proxmox to apply supported "+key+" changes while the VM runs. Guest and device support are also required.");features[key]=box;
        }
        readers.Add((c,_)=>{
            if(!hardware || features.All(p=>(p.Value.IsChecked==true)==current.Contains(p.Key)))return;
            var selected=current.Where(k=>!features.ContainsKey(k)).Concat(features.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key)).ToArray();
            c["hotplug"]=selected.Length==0?"0":string.Join(",",selected);
        });
        dialog.Tab("Advanced");
        Flag("acpi","ACPI support",true,hardware,"Expose ACPI power management to the guest.");
        Flag("kvm","KVM hardware virtualization",true,hardware,"Use host hardware acceleration for this VM.");
        Flag("freeze","Freeze CPU at startup",false,power,"Start the VM with its CPUs paused until explicitly resumed.");
        Choice("localtime","Real-time clock",new[]{"Default","0","1"},"Default",options,"Choose which time the VM’s hardware clock shows. The default follows the guest operating system.");
        foreach(var (key,label,editable,help) in new[]{("startdate","RTC start date",options,"Use now or an ISO date accepted by Proxmox."),("smbios1","SMBIOS type 1 settings",hardware,"Proxmox property string for firmware identity. Preserve existing UUID and encoded fields unless deliberately changing them.")}){
            string initial=cfg.Get(key);var box=dialog.Text(label,initial);box.IsEnabled=editable;dialog.Help(help);
            readers.Add((c,del)=>{var raw=box.Text.Trim();if(editable && raw!=initial){if(raw=="")del.Add(key);else c[key]=raw;}});
        }
        return (c,del)=>{foreach(var read in readers)read(c,del);};
    }
}
