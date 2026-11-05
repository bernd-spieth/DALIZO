// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
namespace ProxmoxSpice;
public partial class MainWindow
{
    void CheckFolderDropRoutes(Border target,Guid id)
    {
        var original=data.Vms.Single(v=>v.Id==id).Folder;
        var payload=new DataObject(VmDragFormat,id.ToString());
        DragEventArgs Event(RoutedEvent routed)=> (DragEventArgs)NewEvent(routed);
        object NewEvent(RoutedEvent routed){var args=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{payload,DragDropKeyStates.LeftMouseButton,DragDropEffects.Move,target,new Point(5,5)},null)!;args.RoutedEvent=routed;return args;}
        try {
            // External payloads, even with the same format, are not accepted.
            activeVmDrag=null;if(CanMoveDraggedVm(payload,DropFolder(target),out _))throw new Exception("External drag accepted.");
            activeVmDrag=id;
            if(CanMoveDraggedVm(payload,original,out _) || CanMoveDraggedVm(payload,"Deleted folder",out _))throw new Exception("Invalid drag destination accepted.");
            var over=Event(DragDrop.DragOverEvent);target.RaiseEvent(over);
            if(over.Effects!=DragDropEffects.Move || data.Vms.Single(v=>v.Id==id).Folder!=original)throw new Exception("Drag hover mutated membership or failed to accept target.");
            string expected=DropFolder(target)!;
            var drop=Event(DragDrop.DropEvent);target.RaiseEvent(drop);
            if(drop.Effects!=DragDropEffects.Move || data.Vms.Single(v=>v.Id==id).Folder!=expected || store.Load().Vms.Single(v=>v.Id==id).Folder!=expected)throw new Exception($"Routed drop mismatch: effect={drop.Effects}, expected={expected}, actual={data.Vms.Single(v=>v.Id==id).Folder}, status={StatusText.Text}");
            var ungroup=Event(DragDrop.DropEvent);UngroupedDropTarget.RaiseEvent(ungroup);
            if(data.Vms.Single(v=>v.Id==id).Folder!="" || store.Load().Vms.Single(v=>v.Id==id).Folder!="")throw new Exception("Ungrouped drop failed.");
            MoveVmToFolder(id,original);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"drag-drop-result.txt"),"PASS: real WPF DragOver/Drop routes accept an internal VM move, hover does not mutate, drop persists membership, Ungrouped drop persists removal; same folder, missing folder and external payload rejected. Synthetic data/events; OS mouse-drag gesture not automated.");
        } finally {activeVmDrag=null;target.ClearValue(Border.BackgroundProperty);highlightedDropTargets.Clear();}
    }
}


