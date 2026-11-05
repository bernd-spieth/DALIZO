// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ProxmoxSpice;
public partial class MainWindow
{
    readonly ListBox Servers=new() {DisplayMemberPath="Name",MinHeight=100,MaxHeight=260};
    void ManageServersClicked(object sender,RoutedEventArgs e)=>Guard(()=> {
        OptionsToggle.IsChecked=false;
        if(busy||closing)return;
        var dialog=new EditorDialog(this,"Manage servers","Close");
        dialog.Note("Add a Proxmox server, or select an existing server to edit or remove its local profile.");
        dialog.AddField(Servers);
        var address=new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)};
        address.SetBinding(TextBlock.TextProperty,new Binding("SelectedItem.Address"){Source=Servers});dialog.AddField(address);
        var actions=new WrapPanel {Orientation=Orientation.Horizontal};dialog.AddField(actions);
        var add=new Button {Content="Add server…",Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,0,8,0)};
        var edit=new Button {Content="Edit server…",Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,0,8,0)};
        var remove=new Button {Content="Remove server…",Padding=new Thickness(12,7,12,7)};
        actions.Children.Add(add);actions.Children.Add(edit);actions.Children.Add(remove);
        var repair=new Button {Content="Repair permissions…",Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,8,0,0)}; dialog.AddField(repair); repair.Click+=(_,_)=>Guard(()=>{if(Servers.SelectedItem is ServerProfile server)RepairServerAccess(dialog,server);});
        void UpdateButtons(){edit.IsEnabled=remove.IsEnabled=repair.IsEnabled=Servers.SelectedItem is ServerProfile;}
        SelectionChangedEventHandler selection=(_,_)=>UpdateButtons();Servers.SelectionChanged+=selection;UpdateButtons();
        add.Click+=(_,_)=>Guard(()=>{ChooseServerSetup(dialog);UpdateButtons();});
        edit.Click+=(_,_)=>Guard(()=>{if(Servers.SelectedItem is ServerProfile server)EditServerProfile(server,dialog);});
        remove.Click+=(_,_)=>RemoveServer(dialog,new RoutedEventArgs());
        try {dialog.ShowDialog();}
        finally {Servers.SelectionChanged-=selection;if(Servers.Parent is Panel parent)parent.Children.Remove(Servers);}
    });
}

