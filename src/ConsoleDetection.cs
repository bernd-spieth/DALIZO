// SPDX-License-Identifier: GPL-3.0-only
namespace ProxmoxSpice;
public partial class MainWindow
{
    readonly HashSet<Guid> templates=new();
    sealed record ConsoleEntry(VmConsoleInfo? Info,DateTimeOffset Checked);
    readonly Dictionary<Guid,ConsoleEntry> consoleInfo=new();
    string ConsoleLabel(SavedVm vm)
    {
        if(!consoleInfo.TryGetValue(vm.Id,out var entry))return "Checking …";
        if(entry.Info==null || DateTimeOffset.UtcNow-entry.Checked>TimeSpan.FromSeconds(90))return "Unknown";
        try { var type=entry.Info.Resolve(vm.ConsoleType); return type=="Serial terminal"?"Serial":type; } catch { return entry.Info.DisplayType=="none" ? "No display" : "Incompatible"; }
    }
    void ApplyInventory(ServerProfile server,IReadOnlyList<Vm> inventory)
    {
        foreach(var saved in data.Vms.Where(v=>v.ServerId==server.Id)) {
            if(inventory.Any(v=>v.Id==saved.VmId && v.IsTemplate))templates.Add(saved.Id);
            else templates.Remove(saved.Id);
        }
        if(targetVm!=null && templates.Contains(targetVm.Id)) { operation?.Cancel(); SetTarget(null); }
    }
    async Task RefreshConsoleInfo(ProxmoxApi api,ServerProfile server,IReadOnlyList<Vm> inventory)
    {
        // Drain all due entries, with bounded concurrency and no timer gaps between batches.
        var due=data.Vms.Where(v=>v.ServerId==server.Id && !templates.Contains(v.Id))
            .Where(v=>!consoleInfo.TryGetValue(v.Id,out var c)||DateTimeOffset.UtcNow-c.Checked>TimeSpan.FromSeconds(60))
            .OrderByDescending(v=>v.Id==targetVm?.Id).ThenBy(v=>consoleInfo.GetValueOrDefault(v.Id)?.Checked ?? DateTimeOffset.MinValue).ToArray();
        using var limit=new SemaphoreSlim(4);
        await Task.WhenAll(due.Select(async saved=>{
            await limit.WaitAsync(monitoringLifetime.Token);
            try {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(monitoringLifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(8));
            VmConsoleInfo? info=null;
            var current=inventory.FirstOrDefault(v=>v.Id==saved.VmId && !v.IsTemplate);
            if(current!=null)try { info=await api.GetConsoleInfoAsync(current,timeout.Token); } catch { }
            if(!closing && data.Servers.Contains(server) && data.Vms.Contains(saved)) {
                consoleInfo[saved.Id]=new(info,DateTimeOffset.UtcNow);
                if(info?.IsTemplate==true)templates.Add(saved.Id);
                Filter();
            }
            } finally {limit.Release();}
        }));
        if(targetVm!=null && templates.Contains(targetVm.Id)) { operation?.Cancel(); SetTarget(null); }
    }
}



