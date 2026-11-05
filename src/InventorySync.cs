// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
namespace ProxmoxSpice;
public partial class MainWindow
{
    readonly HashSet<Guid> inventoryHidden=new();
    readonly Dictionary<Guid,DateTimeOffset> lastInventorySync=new();
    async Task SyncInventory(ProxmoxApi api,ServerProfile server,IReadOnlyList<Vm> inventory)
    {
        if(lastInventorySync.TryGetValue(server.Id,out var last)&&DateTimeOffset.UtcNow-last<TimeSpan.FromSeconds(30))return;
        lastInventorySync[server.Id]=DateTimeOffset.UtcNow;
        try {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(monitoringLifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var limit=new SemaphoreSlim(4);
            var usable=await Task.WhenAll(inventory.Where(v=>!v.IsTemplate).Select(async vm=>{
                await limit.WaitAsync(timeout.Token);
                try {return (Vm:vm,Allowed:await api.CanUseVmAsync(vm.Id,timeout.Token));}
                finally {limit.Release();}
            }));
            if(closing||!data.Servers.Contains(server))return;
            var allowed=usable.Where(v=>v.Allowed).Select(v=>v.Vm).ToArray();
            var ids=allowed.Select(v=>v.Id).ToHashSet();
            var next=Snapshot();int added=0;
            foreach(var vm in allowed)
                if(!next.Vms.Any(v=>v.ServerId==server.Id&&v.VmId==vm.Id)) {
                    next.Vms.Add(new(Guid.NewGuid(),server.Id,vm.Id,string.IsNullOrWhiteSpace(vm.Name)?"VM "+vm.Id:vm.Name));added++;
                }
            // Retain aliases/settings when a VM is deleted, inaccessible or temporarily a template.
            // Only a fully successful inventory + permissions pass may hide existing entries.
            if(added>0)Commit(next);
            foreach(var saved in data.Vms.Where(v=>v.ServerId==server.Id)) {
                if(ids.Contains(saved.VmId))inventoryHidden.Remove(saved.Id);else inventoryHidden.Add(saved.Id);
            }
            if(targetVm!=null&&inventoryHidden.Contains(targetVm.Id)) {operation?.Cancel();SetTarget(null);}
            if(added>0)Status($"{added} VMs added automatically · {server.Name}");
        }
        catch(OperationCanceledException) when(closing) { }
        catch(Exception error) { if(!closing)ReportError("Automatic VM sync failed. Existing entries are retained.",error); }
    }
    async void RefreshInventoryClicked(object sender,RoutedEventArgs e)
    {
        foreach(var server in data.Servers.ToArray()) {
            if(GetSecret(server)!=null)lastInventorySync.Remove(server.Id);
        }
        if(polling) {Status("VM refresh queued; the current request will finish first.");return;}
        await PollStatus();
    }
    async Task CheckInventorySync(ServerProfile server)
    {
        var original=Snapshot();
        try {
            using var api=new ProxmoxApi(server.Address,new SmokeHttp(req=>
                req.RequestUri!.Query.Contains("106") ? "{\"data\":{\"/vms/106\":{\"VM.Console\":1}}}" :
                req.RequestUri!.Query.Contains("105") ? "{\"data\":{\"/vms/105\":{\"VM.Audit\":1}}}" :
                req.RequestUri.Query.Contains("103") ? "{\"data\":{\"/vms/103\":{\"VM.PowerMgmt\":0}}}" :
                "{\"data\":{\"/vms/101\":{\"VM.Console\":1}}}"));
            var inventory=new[]{new Vm(101,"n","Remote renamed","running"),new Vm(103,"n","New VM","stopped"),new Vm(104,"n","Template","stopped",true),new Vm(105,"n","Audit only","stopped")};
            await SyncInventory(api,server,inventory);Filter();
            if(data.Vms.Count!=original.Vms.Count+1 || data.Vms.Single(v=>v.VmId==101).Name!=original.Vms.Single(v=>v.VmId==101).Name || data.Vms.Single(v=>v.VmId==103).ConsoleType!="Auto")throw new Exception("Automatic inventory merge failed.");
            if(VmList.Items.Cast<VmRow>().Select(r=>r.VmId).Order().SequenceEqual(new[]{101,103})!=true)throw new Exception("Inventory permission/template/missing filter failed.");
            lastInventorySync.Remove(server.Id);await SyncInventory(api,server,inventory);
            if(data.Vms.Count!=original.Vms.Count+1)throw new Exception("Automatic inventory duplicated a VM.");
            var hidden=inventoryHidden.ToHashSet();lastInventorySync.Remove(server.Id);
            using var failed=new ProxmoxApi(server.Address,new SmokeHttp(_=>throw new System.Net.Http.HttpRequestException("Synthetic offline")));
            await SyncInventory(failed,server,inventory);
            if(!hidden.SetEquals(inventoryHidden)||data.Vms.Count!=original.Vms.Count+1)throw new Exception("Failed sync changed known inventory.");
            var changed=new[]{inventory[0],new Vm(106,"n","Discovered later","running")};
            lastInventorySync[server.Id]=DateTimeOffset.UtcNow-TimeSpan.FromSeconds(29);
            await SyncInventory(api,server,changed);Filter();
            if(data.Vms.Any(v=>v.VmId==106)||!VmList.Items.Cast<VmRow>().Any(v=>v.VmId==103))throw new Exception("Inventory refreshed before the 30-second interval.");
            lastInventorySync[server.Id]=DateTimeOffset.UtcNow-TimeSpan.FromSeconds(31);
            await SyncInventory(api,server,changed);Filter();
            if(!VmList.Items.Cast<VmRow>().Select(v=>v.VmId).Order().SequenceEqual(new[]{101,106}))throw new Exception("30-second inventory refresh did not discover addition/deletion.");
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory,"inventory-sync-result.txt"),"PASS: automatic addition; own name and Auto console retained; no duplicates; templates and audit-only entries excluded; missing entries hidden; failed permissions pass preserves prior inventory. Synthetic profiles only.");
        }
        finally { inventoryHidden.Clear();lastInventorySync.Clear();Commit(original); }
    }
}


