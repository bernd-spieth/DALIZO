// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
namespace ProxmoxSpice;
public partial class MainWindow
{
    void VmContextOpening(object sender,ContextMenuEventArgs e)
    {
        VmRow? row=null;string? folder=null;
        var current=e.OriginalSource as DependencyObject;
        while(current!=null && current!=VmList) {
            if(current is DataGridRow r){row=r.Item as VmRow;break;}
            if(current is Expander {DataContext:CollectionViewGroup group}){folder=(string)group.Name;break;}
            current=current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        if(e.CursorLeft<0 && row==null && folder==null)row=VmList.SelectedItem as VmRow;
        VmList.ContextMenu=BuildVmContext(row,folder);
    }
    ContextMenu BuildVmContext(VmRow? row,string? folder)
    {
        var menu=new ContextMenu{Style=(Style)FindResource("UnifiedMenu")};
        menu.Resources[typeof(MenuItem)]=FindResource("UnifiedMenuItem");
        MenuItem Add(string title,Action action,bool enabled=true){var item=new MenuItem{Header=title,IsEnabled=enabled};item.Click+=(_,_)=>{menu.IsOpen=false;Guard(action);};menu.Items.Add(item);return item;}
        bool editable=!busy&&!closing&&typingCancellation==null;
        if(row!=null) {
            UpdateRowPower(row);
            void PowerItem(string label,string action,string enabled,string hint){
                var item=Add(label,()=>RowPowerClicked(new Button{DataContext=row,Tag=action},new RoutedEventArgs(Button.ClickEvent)));
                item.SetBinding(MenuItem.IsEnabledProperty,new Binding(enabled){Source=row});
                item.SetBinding(MenuItem.ToolTipProperty,new Binding(hint){Source=row});
                ToolTipService.SetShowOnDisabled(item,true);
            }
            PowerItem("Start","start",nameof(VmRow.CanStart),nameof(VmRow.StartHint));
            PowerItem("Shut down…","shutdown",nameof(VmRow.CanShutdown),nameof(VmRow.ShutdownHint));
            PowerItem("Restart…","reboot",nameof(VmRow.CanShutdown),nameof(VmRow.RestartHint));
            menu.Items.Add(new Separator());
            Add("Connect",()=>RowConnectClicked(new Button{DataContext=row},new RoutedEventArgs(Button.ClickEvent)),editable&&row.CanConnect);
            Add("Disconnect",()=>CloseConsoleFromUser(row.Id),editable&&row.CanDisconnect);
            Add("VM settings…",()=>RowPropertiesClicked(new Button{DataContext=row},new RoutedEventArgs(Button.ClickEvent)),editable);
            Add("Move to folder…",()=>ChooseVmFolder(row),editable);
            menu.Items.Add(new Separator());
        } else if(folder!=null && folder!="Ungrouped") {
            var stub=new Border{DataContext=VmList.Items.Groups!.Cast<CollectionViewGroup>().First(g=>(string)g.Name==folder)};
            Add("Edit folder…",()=>EditFolderFromHeader(stub,new RoutedEventArgs(Button.ClickEvent)),editable);
            Add("Rename folder…",()=>RenameVmFolder(folder),editable);
            Add("Remove folder…",()=>RemoveFolderFromHeader(stub,new RoutedEventArgs(Button.ClickEvent)),editable);
            menu.Items.Add(new Separator());
        }
        Add("New folder…",()=>NewFolderClicked(this,new RoutedEventArgs(Button.ClickEvent)),editable);
        Add("Refresh VM list",()=>RefreshInventoryClicked(this,new RoutedEventArgs(Button.ClickEvent)),!busy&&!closing);
        return menu;
    }
    void ChooseVmFolder(VmRow row)
    {
        ShowSidebar(false);
        try {
            var dialog=new EditorDialog(this,"Move VM to folder");dialog.Note(row.Name+" · VM "+row.VmId);
            var folders=data.Vms.Select(v=>v.Folder).Append("").Distinct().Order().Select(f=>f.Length==0?"Ungrouped":f).Cast<object>().ToArray();
            var choice=dialog.Choice("Folder",folders,row.Folder.Length==0?"Ungrouped":row.Folder);choice.DisplayMemberPath="";
            if(dialog.ShowDialog()==true && choice.SelectedItem is string name)MoveVmToFolder(row.Id,name=="Ungrouped"?"":name);
        } finally {ShowSidebar(true);}
    }
    void MoveVmToFolder(Guid id,string name)
    {
        Commit(Snapshot() with {Vms=data.Vms.Select(v=>v.Id==id?v with {Folder=name}:v).ToList()});
    }
    void RenameVmFolder(string original)
    {
        ShowSidebar(false);
        try {
            var dialog=new EditorDialog(this,"Rename folder");var name=dialog.Text("Folder name",original);WorkspaceData? next=null;
            dialog.Validate=()=>{var value=name.Text.Trim();if(value.Length==0)throw new InvalidOperationException("Enter a folder name.");if(data.Vms.Any(v=>v.Folder!=original&&v.Folder.Equals(value,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Folder already exists.");next=Snapshot() with {Vms=data.Vms.Select(v=>v.Folder==original?v with {Folder=value}:v).ToList()};ProfileStore.Validate(next);return true;};
            if(dialog.ShowDialog()==true && next!=null){if(collapsedFolders.Remove(original))collapsedFolders.Add(name.Text.Trim());Commit(next);}
        } finally {ShowSidebar(true);}
    }
}



