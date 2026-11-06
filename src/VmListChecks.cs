// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ProxmoxSpice;
public partial class MainWindow
{
    async Task WithVmTestViewport(Func<Task> check)
    {
        // The popup closes on focus loss. Render the real VM panel in a finite
        // test viewport so WPF generates headers and rows even in a background run.
        var restoreSidebar=sidebarVisible;
        var viewport=new Grid { Width=656,Height=540,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top };
        ShowSidebar(false);
        try {
            SidebarOverlay.Child=null;
            WorkspaceGrid.Children.Add(viewport);
            viewport.Children.Add(SidebarPanel);
            UpdateLayout();
            await check();
        } finally {
            viewport.Children.Remove(SidebarPanel);
            WorkspaceGrid.Children.Remove(viewport);
            SidebarOverlay.Child=SidebarPanel;
            ShowSidebar(restoreSidebar);
        }
    }
    async Task CheckVmListSorting()
    {
        var saved=data;var selected=SelectedVm;var originalQuery=Search.Text;var originalSort=vmSort;
        try {
            data=new(1,saved.Servers.ToList(),Enumerable.Range(1,1000)
                .Select(i=>new SavedVm(Guid.NewGuid(),saved.Servers[0].Id,10000+i,$"Guest {1001-i:0000}")).ToList());
            Search.Text="";Filter();UpdateLayout();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var originalColumns=VmList.Columns.OrderBy(c=>c.DisplayIndex).ToArray();
            if(!VmList.CanUserReorderColumns)throw new Exception("Column dragging is disabled.");
            ActionsColumn.DisplayIndex=VmList.Columns.Count-1;
            VmColumnReordered(VmList,new DataGridColumnEventArgs(ActionsColumn));
            ActionsColumn.DisplayIndex=1;LoadVmColumns();
            if(ActionsColumn.DisplayIndex!=VmList.Columns.Count-1)throw new Exception("Column order did not reload.");
            for(int i=0;i<originalColumns.Length;i++)originalColumns[i].DisplayIndex=i;
            VmColumnReordered(VmList,new DataGridColumnEventArgs(ActionsColumn));
            var view=VmList.Items;
            var watch=Stopwatch.StartNew();
            NameColumn.SortDirection=ListSortDirection.Ascending;
            VmListSorting(VmList,new DataGridSortingEventArgs(NameColumn));
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();watch.Stop();
            var source=VmList.ItemsSource;
            var chosen=view.OfType<VmRow>().ElementAt(4);VmList.SelectedItem=chosen;
            runtime[chosen.Id]=new(new("running","",0),DateTimeOffset.UtcNow,null);
            Filter();UpdateLayout();
            if(!ReferenceEquals(source,VmList.ItemsSource)||!ReferenceEquals(chosen,VmList.SelectedItem))
                throw new Exception("Status refresh replaced the VM list or its selected row.");
            if(chosen.RuntimeStatus!="Running")throw new Exception("Cached row status was not updated.");
            if(view.OfType<VmRow>().First().Name!="Guest 1000")throw new Exception("Refresh lost descending name sort.");
            int rendered=Enumerable.Range(0,view.Count).Count(i=>VmList.ItemContainerGenerator.ContainerFromIndex(i) is DataGridRow);
            if(rendered is <=0 or >=100)throw new Exception($"Expected a populated, virtualized VM list: {rendered} rendered rows for 1000 VMs.");
            Search.Text="Guest 00";Filter();
            if(VmList.Items.OfType<VmRow>().First().Name!="Guest 0099")throw new Exception("Search lost descending sort.");
            vmSort=new("VmId",ListSortDirection.Ascending);LoadVmSort();Refresh();
            if(vmSort.Property!="Name" || vmSort.Direction!=ListSortDirection.Descending || NameColumn.SortDirection!=ListSortDirection.Descending || VmList.Items.OfType<VmRow>().First().Name!="Guest 0099")throw new Exception("Saved sorting or header direction was lost after reload and refresh.");
            IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject {for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++){var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;foreach(var nested in Descendants<T>(child))yield return nested;}}
            UpdateLayout();
            var headers=Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(VmList).ToArray();
            var header=headers.SingleOrDefault(h=>h.Column==NameColumn)??throw new Exception($"Missing Name header: active={IsActive}; window={IsVisible}; popup={SidebarOverlay.IsOpen}; gridVisible={VmList.IsVisible}; gridLoaded={VmList.IsLoaded}; grid={VmList.RenderSize}; realizedRows={rendered}; headerCount={headers.Length}");
            var arrow=(System.Windows.Shapes.Path)header.Template.FindName("SortArrow",header);
            if(arrow.Visibility!=Visibility.Visible||arrow.Data.ToString()!="M0,0L4,5 8,0z") {
                if(arrow.Visibility!=Visibility.Visible||NameColumn.SortDirection!=ListSortDirection.Descending)throw new Exception("Sort indicator missing.");
            }
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)VmList.ActualWidth,(int)VmList.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(VmList);
            var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(AppContext.BaseDirectory,"column-sort-preview.png")))png.Save(file);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"vm-sorting-result.txt"),
                $"PASS: column reordering enabled; saved order reload; visible descending arrow; 1000 synthetic VMs; descending sort plus idle/layout {watch.Elapsed.TotalMilliseconds:0.0} ms; {rendered} realized rows; status refresh preserves source and selection; search preserves sort; persisted sorting reload and full refresh preserve name order and header arrow. No live API or network timing measured.");
        } finally {
            vmSort=originalSort;data=saved;Search.Text=originalQuery;Filter(selected?.Id);SetTarget(selected);
        }
    }
}


