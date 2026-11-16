// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Controls;
namespace ProxmoxSpice;
public partial class MainWindow
{
    static ComboBox AddBridgeChoice(EditorDialog dialog,string key,string current,IReadOnlyList<string> bridges)
    {
        var values=bridges.Append(current).Append("").Distinct(StringComparer.Ordinal).OrderBy(s=>s,StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray();
        var box=dialog.Choice("Bridge / VNet",values,current);
        box.DisplayMemberPath="";box.IsEditable=true;box.Text=current;
        dialog.Help("Select a network or enter its name. Leave blank for a private network: the VM can make outbound connections through the host, and other LAN devices cannot initiate connections to it by default.",false);
        box.ToolTip="Choose a network from this VM's node. The existing value is kept even if it is not returned by the server.";
        return box;
    }
    static Func<string> NetworkFields(EditorDialog dialog,string key,VmNetwork network,IReadOnlyList<string> bridges){
        var bridge=AddBridgeChoice(dialog,key,network.Bridge,bridges);
        TextBox Field(string label,string property,string help){
            var box=dialog.Text(label,network.Get(property));dialog.Help(help,false);return box;
        }
        var vlan=Field("VLAN tag","tag","Leave blank for untagged traffic, or enter a VLAN ID from 1 to 4094.");
        var firewall=dialog.Check("Enable firewall for this adapter",network.Firewall);
        dialog.Help("Allow Proxmox firewall rules to apply to traffic on this interface.",false);
        var connected=dialog.Check("Virtual cable connected",network.Connected);
        dialog.Help("Clear to disconnect the adapter while keeping its settings.",false);
        TextBox mac=null!;
        TextBox rate=null!,queues=null!,mtu=null!;ComboBox model=null!;
        dialog.AdvancedFields(()=>{
            model=dialog.Choice("Adapter model",VmNetwork.Models.Append(network.Model).Distinct().Cast<object>().ToArray(),network.Model);
            model.DisplayMemberPath="";
            dialog.Help("Choose a network card supported by the guest operating system.",false);
            mac=Field("MAC address",network.Model,"Leave blank to let Proxmox assign an address.");
            rate=Field("Rate limit (MB/s)","rate","Leave blank for unlimited bandwidth.");
            queues=Field("Multiqueue","queues","Number of packet queues (1–64). Leave blank for the default.");
            mtu=Field("MTU","mtu","VirtIO only. Enter 576–65520 bytes, 1 to inherit the bridge MTU, or leave blank for the default.");
            mtu.IsEnabled=(string)model.SelectedItem=="virtio";
            model.SelectionChanged+=(_,_)=>mtu.IsEnabled=(string)model.SelectedItem=="virtio";
        });
        return ()=>network.ChangeAdapter(bridge.Text,connected.IsChecked==true,(string)model.SelectedItem,mac.Text,vlan.Text,
            firewall.IsChecked==true,rate.Text,queues.Text,(string)model.SelectedItem=="virtio"?mtu.Text:network.Model==(string)model.SelectedItem?network.Get("mtu"):"");
    }
}
