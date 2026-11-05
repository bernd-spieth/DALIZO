// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
namespace ProxmoxSpice;
public partial class MainWindow
{
    const string VmDragFormat="DALIZO.LocalVmMove";
    Guid? dragCandidate,activeVmDrag;
    Point dragOrigin;
    long lastDragScroll;
    readonly HashSet<Border> highlightedDropTargets=new();
    static DependencyObject? DragParent(DependencyObject child)=>child is Visual?VisualTreeHelper.GetParent(child):LogicalTreeHelper.GetParent(child);
    void VmDragMouseDown(object sender,MouseButtonEventArgs e)
    {
        dragCandidate=null;
        if(e.ClickCount!=1||busy||closing||typingCancellation!=null)return;
        var element=e.OriginalSource as DependencyObject;
        while(element!=null && element!=VmList){
            if(element is ButtonBase or DataGridColumnHeader)return;
            if(element is DataGridRow {Item:VmRow row}){dragCandidate=row.Id;dragOrigin=e.GetPosition(VmList);return;}
            element=DragParent(element);
        }
    }
    void VmDragMouseMove(object sender,MouseEventArgs e)
    {
        if(e.LeftButton!=MouseButtonState.Pressed){dragCandidate=null;return;}
        if(dragCandidate is not Guid id||activeVmDrag!=null||busy||closing||typingCancellation!=null)return;
        var position=e.GetPosition(VmList);
        if(Math.Abs(position.X-dragOrigin.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y-dragOrigin.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        dragCandidate=null;activeVmDrag=id;e.Handled=true;
        bool stay=SidebarOverlay.StaysOpen;SidebarOverlay.StaysOpen=true;UngroupedDropTarget.Visibility=Visibility.Visible;
        try {DragDrop.DoDragDrop(VmList,new DataObject(VmDragFormat,id.ToString()),DragDropEffects.Move);}
        finally {
            activeVmDrag=null;dragCandidate=null;
            foreach(var target in highlightedDropTargets)target.ClearValue(Border.BackgroundProperty);
            highlightedDropTargets.Clear();UngroupedDropTarget.Visibility=Visibility.Collapsed;SidebarOverlay.StaysOpen=stay;
        }
    }
    string? DropFolder(Border target)=>target==UngroupedDropTarget?"":target.DataContext is System.Windows.Data.CollectionViewGroup group ? (string)group.Name=="Ungrouped"?"":(string)group.Name:null;
    bool CanMoveDraggedVm(IDataObject payload,string? folder,out Guid id)
    {
        id=Guid.Empty;
        if(busy||closing||typingCancellation!=null||activeVmDrag is not Guid active||folder==null)return false;
        if(!payload.GetDataPresent(VmDragFormat,false)||payload.GetData(VmDragFormat,false) is not string text||!Guid.TryParse(text,out id)||id!=active)return false;
        var vm=data.Vms.FirstOrDefault(v=>v.Id==active);
        return vm!=null && vm.Folder!=folder && (folder.Length==0||data.Vms.Any(v=>v.Folder==folder));
    }
    void FolderDragOver(object sender,DragEventArgs e)
    {
        e.Handled=true;e.Effects=DragDropEffects.None;
        if(sender is not Border target)return;
        bool allowed=(e.AllowedEffects&DragDropEffects.Move)!=0&&CanMoveDraggedVm(e.Data,DropFolder(target),out _);
        if(allowed){e.Effects=DragDropEffects.Move;target.Background=new SolidColorBrush(Color.FromRgb(216,234,252));highlightedDropTargets.Add(target);}
        else {target.ClearValue(Border.BackgroundProperty);highlightedDropTargets.Remove(target);}
    }
    void FolderDragLeave(object sender,DragEventArgs e)
    {
        if(sender is Border target){target.ClearValue(Border.BackgroundProperty);highlightedDropTargets.Remove(target);}
    }
    void FolderDrop(object sender,DragEventArgs e)
    {
        e.Handled=true;e.Effects=DragDropEffects.None;
        if(sender is not Border target)return;
        target.ClearValue(Border.BackgroundProperty);highlightedDropTargets.Remove(target);
        var folder=DropFolder(target);
        if((e.AllowedEffects&DragDropEffects.Move)==0||!CanMoveDraggedVm(e.Data,folder,out var id))return;
        try {
            MoveVmToFolder(id,folder!);e.Effects=DragDropEffects.Move;
            Status("VM moved to "+(folder!.Length==0?"Ungrouped":folder)+". Saved locally.");
        } catch(Exception error){ReportError("Could not move VM:",error);}
    }
    void VmListDragOver(object sender,DragEventArgs e){e.Effects=DragDropEffects.None;e.Handled=true;}
    void VmDragScroll(object sender,DragEventArgs e)
    {
        if(activeVmDrag==null||Environment.TickCount64-lastDragScroll<150)return;
        var y=e.GetPosition(VmList).Y;
        if(y>40 && y<VmList.ActualHeight-28)return;
        ScrollViewer? Find(DependencyObject element){for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++){var child=VisualTreeHelper.GetChild(element,i);if(child is ScrollViewer s)return s;var found=Find(child);if(found!=null)return found;}return null;}
        var scroll=Find(VmList);if(scroll==null)return;
        if(y<=40)scroll.LineUp();else scroll.LineDown();lastDragScroll=Environment.TickCount64;
    }
}


