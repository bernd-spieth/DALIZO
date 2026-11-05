// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
namespace ProxmoxSpice;
public partial class MainWindow
{
    async void RowPowerClicked(object sender,RoutedEventArgs e)
    {
        e.Handled=true;
        if(busy||closing||typingCancellation!=null||sender is not Button {DataContext:VmRow row,Tag:string action})return;
        UpdateRowPower(row);
        if(action=="start"?!row.CanStart:action is "shutdown" or "reboot"?!row.CanShutdown:true)return;
        var vm=data.Vms.FirstOrDefault(v=>v.Id==row.Id);if(vm==null)return;
        VmList.SelectedItem=row;
        await ExecuteVmPower(vm,action);
    }
    void UpdateRowPower(VmRow row)
    {
        runtime.TryGetValue(row.Id,out var entry);
        bool ready=!busy&&!closing&&typingCancellation==null&&entry?.Fresh==true;
        string reason=busy||typingCancellation!=null?"An operation is in progress.":entry?.Fresh!=true?"Wait for a current VM status.":"VM status: "+entry.Label+".";
        bool start=ready&&entry!.State!.Status=="stopped";
        bool shutdown=ready&&entry!.State!.Status=="running"&&entry.State.Label=="Running";
        row.SetPowerActions(start,shutdown,start?"Start VM": "Start unavailable: "+reason,shutdown?"Shut down VM gracefully (confirmation required)":"Shutdown unavailable: "+reason);
    }
}

