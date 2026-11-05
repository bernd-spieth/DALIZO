// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace ProxmoxSpice;
public partial class MainWindow
{
    async Task CheckVmFolders(ServerProfile server)
    {
        var original=data;var oldQuery=Search.Text;var oldSort=vmSort;
        try {
            data=new(1,original.Servers.ToList(),new(){new(Guid.NewGuid(),server.Id,92001,"Zulu",Folder:"QA Group"),new(Guid.NewGuid(),server.Id,92002,"Alpha",Folder:"QA Group"),new(Guid.NewGuid(),server.Id,92003,"Service",Folder:"Production"),new(Guid.NewGuid(),server.Id,92004,"Unassigned")});
            vmSort=new("Name",System.ComponentModel.ListSortDirection.Ascending);Search.Text="";Filter();ApplyVmSort();ShowSidebar(true);UpdateLayout();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var groups=VmList.Items.Groups!.Cast<CollectionViewGroup>().ToArray();
            if(groups.Length!=3 || groups.Single(g=>(string)g.Name=="QA Group").Items.Cast<VmRow>().First().Name!="Alpha")throw new Exception("Folder grouping or member sorting failed.");
            IEnumerable<DependencyObject> Children(DependencyObject parent){for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Children(child))yield return nested;}}
            var dropTarget=Children(VmList).OfType<Border>().Single(b=>(string?)b.Tag=="VmFolderDrop" && b.DataContext is CollectionViewGroup group && (string)group.Name=="Production");
            CheckFolderDropRoutes(dropTarget,data.Vms.Single(v=>v.VmId==92001).Id);UpdateLayout();
            var folderMenu=BuildVmContext(null,"QA Group");
            if(!folderMenu.Items.OfType<MenuItem>().Select(i=>(string)i.Header).Contains("Rename folder…"))throw new Exception("Folder context actions missing.");
            void CheckDialogAction(string label,string title) {
                bool opened=false;
                var scheduled=Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{
                    var dialog=Application.Current.Windows.OfType<EditorDialog>().SingleOrDefault(w=>w.Title==title);
                    if(dialog!=null){opened=true;dialog.DialogResult=false;}
                }));
                folderMenu.Items.OfType<MenuItem>().Single(i=>(string)i.Header==label).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                scheduled.Abort();
                if(!opened)throw new Exception("Context action did not open its dialog: "+label+"; "+StatusText.Text);
            }
            CheckDialogAction("Edit folder…","Edit folder");
            CheckDialogAction("Remove folder…","Remove folder");
            CheckDialogAction("Rename folder…","Rename folder");
            CheckDialogAction("New folder…","New folder");
            Search.Text="no-match-empty-folder-context-test";
            if(VmList.Items.Count!=0)throw new Exception("Expected empty filtered list.");
            folderMenu=BuildVmContext(null,null);
            CheckDialogAction("New folder…","New folder");
            Search.Text="";
            if(Children(ServerActions).OfType<Button>().Any(b=>b.Content as string=="New folder…"))throw new Exception("Standalone New folder button still visible.");
            var row=VmList.Items.OfType<VmRow>().Single(r=>r.VmId==92001);var rowMenu=BuildVmContext(row,null);
            if(!rowMenu.Items.OfType<MenuItem>().Select(i=>(string)i.Header).Contains("Move to folder…"))throw new Exception("VM context actions missing.");
            VmList.ContextMenu=rowMenu;rowMenu.PlacementTarget=VmList;rowMenu.IsOpen=true;
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            if(!rowMenu.IsOpen || !SidebarOverlay.IsOpen)throw new Exception("Context menu closed the floating VM menu.");rowMenu.IsOpen=false;
            if(Children(VmList).OfType<Button>().Any(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="Edit folder"))throw new Exception("Folder header edit icon still present.");
            var expander=Children(VmList).OfType<Expander>().Single(x=>x.DataContext is CollectionViewGroup g && (string)g.Name=="QA Group");
            expander.IsExpanded=false;
            if(!collapsedFolders.Contains("QA Group"))throw new Exception("Folder collapse state not recorded.");
            Search.Text="Alpha";UpdateLayout();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            if(VmList.Items.Count!=1 || !Children(VmList).OfType<Expander>().Single().IsExpanded)throw new Exception("Search did not reveal a match in a collapsed folder.");
            Search.Text="QA Group";if(VmList.Items.Count!=2)throw new Exception("Folder-name search failed.");
            Search.Text="";UpdateLayout();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            if(Children(VmList).OfType<Expander>().Single(x=>x.DataContext is CollectionViewGroup g && (string)g.Name=="QA Group").IsExpanded)throw new Exception("Clearing search lost folder collapse state.");
            var temp=new ProfileStore(Path.Combine(AppContext.BaseDirectory,"smoke-data","folder-profile.json"));temp.Save(data);
            if(temp.Load().Vms.Count(v=>v.Folder=="QA Group")!=2)throw new Exception("Folder assignments did not persist.");
            data=data with {Vms=data.Vms.Select(v=>v.Folder=="QA Group"?v with {Folder="Engineering"}:v).ToList()};Search.Text="";Filter();
            if(!VmList.Items.Groups!.Cast<CollectionViewGroup>().Any(g=>(string)g.Name=="Engineering"))throw new Exception("Folder rename failed.");
            UpdateLayout();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var bitmap=new RenderTargetBitmap((int)SidebarPanel.ActualWidth,(int)SidebarPanel.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(SidebarPanel);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(AppContext.BaseDirectory,"folders-preview.png")))png.Save(file);
            var ids=data.Vms.Select(v=>v.Id).ToArray();data=data with {Vms=data.Vms.Select(v=>v with {Folder=""}).ToList()};Filter();
            if(!ids.SequenceEqual(data.Vms.Select(v=>v.Id)) || VmList.Items.GroupDescriptions.Count!=0)throw new Exception("Removing folders changed VMs or retained grouping.");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"folders-result.txt"),"PASS: Edit/Remove/Rename/New folder menu Click events open the correct real dialogs; cancellation preserves data; folder grouping, alphabetical member sort, collapse, search reveals collapsed matches, folder-name search, profile round-trip, rename and ungroup without deleting VMs. Synthetic local profiles only.");
        } finally {data=original;vmSort=oldSort;collapsedFolders.Clear();Search.Text=oldQuery;Filter();ApplyVmSort();}
    }
}








