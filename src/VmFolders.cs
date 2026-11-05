// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
namespace ProxmoxSpice;
public partial class MainWindow
{
    readonly HashSet<string> collapsedFolders=new(StringComparer.Ordinal);
    void FolderLoaded(object sender,RoutedEventArgs e)
    {
        if(sender is Expander {DataContext:CollectionViewGroup group} expander)
            expander.IsExpanded=Search.Text.Length>0 || !collapsedFolders.Contains((string)group.Name);
    }
    void FolderExpanded(object sender,RoutedEventArgs e)
    {
        if(ReferenceEquals(e.OriginalSource,sender) && Search.Text.Length==0 && sender is Expander {IsLoaded:true,DataContext:CollectionViewGroup group})collapsedFolders.Remove((string)group.Name);
    }
    void FolderCollapsed(object sender,RoutedEventArgs e)
    {
        if(ReferenceEquals(e.OriginalSource,sender) && Search.Text.Length==0 && sender is Expander {IsLoaded:true,DataContext:CollectionViewGroup group})collapsedFolders.Add((string)group.Name);
    }
    void NewFolderClicked(object sender,RoutedEventArgs e)
    {
        e.Handled=true;ShowSidebar(false);
        try {EditFolder(this,null);} finally {ShowSidebar(true);}
    }
    void EditFolderFromHeader(object sender,RoutedEventArgs e)
    {
        e.Handled=true;
        if(sender is not FrameworkElement {DataContext:CollectionViewGroup group} || (string)group.Name=="Ungrouped")return;
        ShowSidebar(false);
        try {EditFolder(this,(string)group.Name);} finally {ShowSidebar(true);}
    }
    void RemoveFolderFromHeader(object sender,RoutedEventArgs e)
    {
        e.Handled=true;
        if(sender is not FrameworkElement {DataContext:CollectionViewGroup group} || (string)group.Name=="Ungrouped")return;
        string folder=(string)group.Name;ShowSidebar(false);
        try {
            if(!EditorDialog.Confirm(this,$"Remove folder ‘{folder}’? Its VMs will move to Ungrouped. No VM will be deleted.","Remove folder"))return;
            Commit(Snapshot() with {Vms=data.Vms.Select(v=>v.Folder==folder?v with {Folder=""}:v).ToList()});collapsedFolders.Remove(folder);
        } finally {ShowSidebar(true);}
    }
    sealed record FolderVmChoice(Guid Id,string Caption);
    void EditFolder(Window owner,string? original)
    {
        var dialog=new EditorDialog(owner,original==null ? "New folder" : "Edit folder");
        var name=dialog.Text("Folder name",original??"");
        dialog.Note("Select the VMs for this folder. Assignments are local to DALIZO. A VM can belong to one folder; VMs removed from this folder become Ungrouped.");
        var choices=data.Vms.OrderBy(v=>v.Name).Select(v=>new FolderVmChoice(v.Id,$"{v.Name} · VM {v.VmId}"+(v.Folder.Length>0?$" · {v.Folder}":""))).ToArray();
        var list=dialog.List("VMs (Ctrl/Shift for multiple selection)",choices.Cast<object>().ToArray());
        foreach(var choice in choices.Where(c=>original!=null && data.Vms.Single(v=>v.Id==c.Id).Folder==original))list.SelectedItems.Add(choice);
        WorkspaceData? next=null;
        dialog.Validate=()=> {
            var title=name.Text.Trim();
            if(title.Length==0)throw new InvalidOperationException("Enter a folder name.");
            if(data.Vms.Any(v=>v.Folder!=original && v.Folder.Equals(title,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("That folder already exists. Edit it to change its members.");
            var ids=list.SelectedItems.Cast<FolderVmChoice>().Select(c=>c.Id).ToHashSet();
            if(ids.Count==0)throw new InvalidOperationException("Select at least one VM for this folder.");
            next=Snapshot() with {Vms=data.Vms.Select(v=>ids.Contains(v.Id)?v with {Folder=title}:original!=null && v.Folder==original?v with {Folder=""}:v).ToList()};
            ProfileStore.Validate(next);return true;
        };
        if(dialog.ShowDialog()==true && next!=null){Commit(next);Status("Folder saved locally.");}
    }
    void ManageFoldersClicked(object sender,RoutedEventArgs e)
    {
        ShowSidebar(false);
        var dialog=new EditorDialog(this,"Manage VM folders","Done","Close");
        dialog.Note("Group VMs locally. Editing or removing a folder does not change VMs in Proxmox. Empty folders are not retained.");
        var list=dialog.Choice("Folder",Array.Empty<object>(),null);list.DisplayMemberPath="";
        void RefreshFolders(){var selected=list.SelectedItem;list.ItemsSource=data.Vms.Select(v=>v.Folder).Where(f=>f.Length>0).Distinct().Order().ToArray();list.SelectedItem=selected;if(list.SelectedIndex<0 && list.Items.Count>0)list.SelectedIndex=0;}
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,12,0,0)};
        var add=new Button{Content="New folder…",Padding=new Thickness(10,6,10,6)};
        var edit=new Button{Content="Edit…",Padding=new Thickness(10,6,10,6),Margin=new Thickness(8,0,8,0)};
        var remove=new Button{Content="Remove",Padding=new Thickness(10,6,10,6)};
        add.Click+=(_,_)=>{EditFolder(dialog,null);RefreshFolders();};
        edit.Click+=(_,_)=>{if(list.SelectedItem is string folder){EditFolder(dialog,folder);RefreshFolders();}};
        remove.Click+=(_,_)=>{
            if(list.SelectedItem is not string folder)return;
            if(!EditorDialog.Confirm(dialog,$"Remove folder ‘{folder}’? Its VMs will move to Ungrouped. No VM will be deleted.","Remove folder"))return;
            Commit(Snapshot() with {Vms=data.Vms.Select(v=>v.Folder==folder?v with {Folder=""}:v).ToList()});collapsedFolders.Remove(folder);RefreshFolders();
        };
        list.SelectionChanged+=(_,_)=>edit.IsEnabled=remove.IsEnabled=list.SelectedItem!=null;
        buttons.Children.Add(add);buttons.Children.Add(edit);buttons.Children.Add(remove);dialog.AddField(buttons);RefreshFolders();edit.IsEnabled=remove.IsEnabled=list.SelectedItem!=null;
        dialog.ShowDialog();ShowSidebar(true);
    }
}


