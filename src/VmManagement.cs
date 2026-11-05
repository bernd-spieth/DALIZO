// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
namespace ProxmoxSpice;

public partial class MainWindow
{
    sealed record RuntimeEntry(VmRuntime? State, DateTimeOffset Checked, string? Error)
    {
        public bool Fresh => State != null && DateTimeOffset.UtcNow - Checked < TimeSpan.FromSeconds(20);
        public string Label => Fresh ? State!.Label : Error ?? "Stale status";
    }
    readonly Dictionary<Guid, RuntimeEntry> runtime = new();
    readonly CancellationTokenSource monitoringLifetime = new();
    DispatcherTimer? monitorTimer;
    SavedVm? targetVm;
    bool polling, refreshingRows, pollingTarget;
    bool smokeMode;
    void InitializeManagement(bool smoke)
    {
        smokeMode = smoke;
        monitorTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(5) };
        monitorTimer.Tick += async (_, _) => { UpdateRuntimeHeader(); await PollStatus(); };
        Loaded += async (_, _) => { if (!smoke) { monitorTimer.Start(); await PollStatus(); } };
        Closed += (_, _) => { monitorTimer.Stop(); monitoringLifetime.Cancel(); };
        UpdateRuntimeHeader();
    }
    string CachedStatus(Guid id) => runtime.TryGetValue(id, out var entry) ? entry.Label : "Not checked";
    void VmSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingRows || closing) return;
        if (SelectedVm is SavedVm vm) SetTarget(vm);
    }
    void SetTarget(SavedVm? vm)
    {
        if (vm == targetVm) return;
        ParkConsole(); targetVm = vm;
        if (vm != null)
        {
            var server = data.Servers.First(s => s.Id == vm.ServerId);
            ConsoleTitle.Text = vm.Name; ConsoleSubtitle.Text = $"{server.Name} · VM {vm.VmId} · {ConsoleLabel(vm)}";
        }
        else { ConsoleTitle.Text = ""; ConsoleSubtitle.Text = "No VM selected"; }
        if(vm == null || !RestoreConsole(vm)) ConsoleArea.Child = new TextBlock { Text="Select a VM and click Connect to open its console.", Margin=new Thickness(24) };
        if(vm==null)ShowConsoleHome();
        UpdateRuntimeHeader();
        if (!smokeMode) _ = PollStatus();
    }
    string? CachedSecret(ServerProfile server)
    {
        if (sessionSecrets.TryGetValue(server.Id, out var secret)) return secret;
        if (server.ProtectedSecret == null) return null;
        try { secret = SecretProtection.Unprotect(server.ProtectedSecret); sessionSecrets[server.Id] = secret; return secret; } catch { return null; }
    }
    async Task PollTargetRuntime()
    {
        if (pollingTarget || closing || targetVm is not SavedVm selected) return;
        var server = data.Servers.FirstOrDefault(s => s.Id == selected.ServerId);
        if (server == null) return;
        pollingTarget = true;
        var started = DateTimeOffset.UtcNow;
        try
        {
            var secret = CachedSecret(server);
            if (secret == null) { runtime[selected.Id] = new(null, started, "Token required"); return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(monitoringLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var api = apiFactory(server, secret);
            var vm = await api.ResolveVmAsync(selected.VmId, timeout.Token);
            var state = await api.GetRuntimeAsync(vm, timeout.Token);
            // An earlier request must not overwrite the readback of a newer power action.
            if (!closing && data.Vms.Contains(selected) &&
                (!runtime.TryGetValue(selected.Id, out var newer) || newer.Checked <= started))
                runtime[selected.Id] = new(state, DateTimeOffset.UtcNow, null);
        }
        catch
        {
            if (!closing && data.Vms.Contains(selected) &&
                (!runtime.TryGetValue(selected.Id, out var newer) || newer.Checked <= started))
                runtime[selected.Id] = new(null, DateTimeOffset.UtcNow, "Unreachable");
        }
        finally
        {
            pollingTarget = false;
            if (!closing) { Filter(); UpdateRuntimeHeader(); }
        }
    }
    async Task PollStatus()
    {
        if(closing)return;
        var targetRefresh=PollTargetRuntime();
        if(polling) { await targetRefresh; return; }
        polling = true;
        try
        {
            var selected = targetVm;
            foreach (var server in data.Servers.OrderByDescending(s => s.Id == selected?.ServerId).ToArray())
            {
                if (closing) break;
                var favorites = data.Vms.Where(v => v.ServerId == server.Id).ToArray();
                var secret = CachedSecret(server);
                if (secret == null) { foreach (var v in favorites) runtime[v.Id] = new(null, DateTimeOffset.UtcNow, "Token required"); continue; }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(monitoringLifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    using var api = apiFactory(server, secret); var all = await api.ListVmsAsync(timeout.Token, includeTemplates:true);
                    if (closing) return;
                    if (!data.Servers.Contains(server)) continue;
                    ApplyInventory(server,all);
                    foreach (var saved in favorites.Where(v => data.Vms.Contains(v) && v.Id != targetVm?.Id))
                    {
                        var vm = all.FirstOrDefault(v => v.Id == saved.VmId);
                        runtime[saved.Id] = vm == null ? new(null, DateTimeOffset.UtcNow, "Not visible") : new(new(vm.Status, "", 0), DateTimeOffset.UtcNow, null);
                    }
                    // Publish the cheap bulk status before permissions/configuration requests.
                    Filter(); UpdateRuntimeHeader();
                    var consoleRefresh=RefreshConsoleInfo(api,server,all);
                    try { if(!smokeMode)await SyncInventory(api,server,all); }
                    finally { await consoleRefresh; }
                }
                catch { if (!closing && data.Servers.Contains(server)) foreach (var v in favorites.Where(v => data.Vms.Contains(v) && v.Id != targetVm?.Id)) runtime[v.Id] = new(null, DateTimeOffset.UtcNow, "Unreachable"); }
            }
        }
        finally { await targetRefresh; polling = false; if (!closing) { Filter(); UpdateRuntimeHeader(); } }
    }
    void UpdateRuntimeHeader()
    {
        if (RuntimeLabel == null || closing) return;
        VmToolbar.Visibility=targetVm==null ? Visibility.Collapsed : Visibility.Visible;
        UpdateConnectButtons();
        RuntimeEntry? entry = null; if (targetVm != null) runtime.TryGetValue(targetVm.Id, out entry);
        var label = targetVm == null ? "No VM selected" : entry?.Label ?? "Not checked";
        RuntimeLabel.Visibility = Visibility.Collapsed;
        RuntimeLabel.Text = targetVm == null ? "" : StatusSymbolFor(label);
        RuntimeLabel.ToolTip = "VM status: " + label;
        System.Windows.Automation.AutomationProperties.SetName(RuntimeLabel, "VM status: " + label);
        RuntimeLabel.Foreground = StatusBrushFor(label);
        RuntimeTimestamp.Text = entry != null ? "Last checked: " + entry.Checked.ToLocalTime().ToString("HH:mm:ss") + " · Refresh every 5 s" : "Status is independent of the console connection";
        var ready = !busy && targetVm != null && entry?.Fresh == true;
        StartButton.IsEnabled = ready && entry!.State!.Status == "stopped";
        ShutdownButton.IsEnabled = RebootButton.IsEnabled = ready && entry!.State!.Status == "running" && entry.State.Label == "Running";
        StopButton.IsEnabled = ready && entry!.State!.Status == "running";

        RefreshStatusButton.IsEnabled = !busy && targetVm != null;
        UpdateDisabledTooltips();
    }
    async void RefreshStatusClicked(object sender, RoutedEventArgs e)
    {
        if (targetVm == null) return; var server = data.Servers.First(s => s.Id == targetVm.ServerId);
        if (GetSecret(server) != null) await PollStatus();
    }
    async void PowerClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string action || targetVm is not SavedVm saved || busy) return;
        await ExecuteVmPower(saved,action);
    }
    async Task ExecuteVmPower(SavedVm saved,string action)
    {
        if(busy||closing||typingCancellation!=null)return;
        var server = data.Servers.First(s => s.Id == saved.ServerId);
        var verb = action switch { "start" => "start", "shutdown" => "shut down", "reboot" => "restart", "stop" => "force stop", _ => throw new InvalidOperationException() };
        // The target is captured before opening any dialog, never read from a later selection.
        var message = $"{verb} VM “{saved.Name}” (ID {saved.VmId}, {server.Name})?";
        if (action == "stop") message += "\n\nThis powers off the VM without a graceful shutdown. Unsaved data may be lost.";
        if (action == "reboot") message += "\n\nThe VM will shut down and restart; pending configuration changes may take effect.";
        if (action != "start" && !EditorDialog.Confirm(this, message, "Confirm VM action")) return;
        var secret = GetSecret(server); if (secret == null) return;
        bool reconnectAfterStart = false;
        await RunOperation(async ct =>
        {
            using var api = apiFactory(server, secret); var current = await api.ResolveVmAsync(saved.VmId, ct);
            var state = await api.GetRuntimeAsync(current, ct); runtime[saved.Id] = new(state, DateTimeOffset.UtcNow, null); UpdateRuntimeHeader();
            if (action == "start" && state.Status != "stopped") throw new InvalidOperationException("The VM is not stopped. Status refreshed; no action submitted.");
            if (action != "start" && state.Status != "running") throw new InvalidOperationException("The VM is not running. Status refreshed; no action submitted.");
            if (action is "shutdown" or "reboot" && state.Label != "Running") throw new InvalidOperationException("The guest is not running. Status refreshed; no action submitted.");
            Status($"VM {saved.VmId}: requesting {verb} …");
            var job = await api.PowerAsync(current, action, ct);
            await WaitForJob(api, job, ct);
            current = await api.ResolveVmAsync(saved.VmId, ct); state = await api.GetRuntimeAsync(current, ct); runtime[saved.Id] = new(state, DateTimeOffset.UtcNow, null);
            if (action == "start")
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
                while (state.Label != "Running" && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(500, ct);
                    current = await api.ResolveVmAsync(saved.VmId, ct);
                    state = await api.GetRuntimeAsync(current, ct);
                    runtime[saved.Id] = new(state, DateTimeOffset.UtcNow, null);
                }
                ct.ThrowIfCancellationRequested();
                if (state.Label != "Running") throw new InvalidOperationException("The start task completed, but the VM is not running yet. Connect its console when it becomes available.");
                reconnectAfterStart = true;
            }
            Status($"Server task completed · VM {saved.VmId}: {state.Label}.");
        });
        if (reconnectAfterStart && !closing && targetVm?.Id == saved.Id)
            await ConnectVm(saved, fresh: true);
        await PollStatus();
    }
    async Task WaitForJob(ProxmoxApi api, ProxmoxJob? job, CancellationToken ct)
    {
        if (job == null) return;
        var until = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested(); var state = await api.GetJobAsync(job, ct);
            if (state.Status == "stopped")
            {
                if (state.ExitStatus != "OK") throw new InvalidOperationException("Server task failed. Check the Proxmox task log (" + (state.ExitStatus ?? "no result") + ").");
                return;
            }
            if (state.Status != "running") throw new InvalidOperationException("Unknown task status. Check the result in Proxmox.");
            Status("Server task running … VM status updates continue."); await Task.Delay(1000, ct);
        }
        throw new InvalidOperationException("Task monitoring timed out. The server task may still be running; check the Proxmox task log.");
    }
    async void ConfigureClicked(object sender, RoutedEventArgs e)
    {
        if (HasDisplayedConsole && targetVm is SavedVm saved) await ConfigureVm(saved);
    }
    async Task ConfigureVm(SavedVm saved)
    {
        if (busy || closing) return;
        var server = data.Servers.First(s => s.Id == saved.ServerId);
        var secret = GetSecret(server); if (secret == null) return;
        await RunOperation(async ct =>
        {
            using var api = apiFactory(server, secret);
            Vm current; VmConfiguration original, active;
            try {
                current = await api.ResolveVmAsync(saved.VmId, ct);
                original = await api.GetConfigurationAsync(current, ct);
                active = await api.GetConfigurationAsync(current, ct, current:true);
            }
            catch (OperationCanceledException) { throw; }
            catch {
                ShowLocalVmSettings(saved);
                return;
            }

            bool canEditNetwork=false;
            try { canEditNetwork=await api.CanEditNetworkAsync(saved.VmId,ct); }
            catch(System.Net.Http.HttpRequestException) { }
            IReadOnlyList<string> bridges=Array.Empty<string>();bool bridgesAvailable=false;
            if(original.Values.Keys.Any(VmNetwork.IsKey)) {
                using var bridgeTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);bridgeTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                try {bridges=await api.GetBridgesAsync(current.Node,bridgeTimeout.Token);bridgesAvailable=true;}
                catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
                catch { }
            }
            var dialog = new EditorDialog(this, $"VM settings · {saved.Name} · {server.Name}");
            dialog.Tab("General");
            dialog.Note("Server configuration includes pending changes. The local display name is independent.");
            var name = dialog.Text("VM name in Proxmox", original.Get("name"));
            var sockets = dialog.Text("CPU sockets", original.Number("sockets", 1).ToString());
            var cores = dialog.Text("Cores per socket", original.Number("cores", 1).ToString());
            var ram = dialog.Text("RAM (MiB)", original.MemoryMiB.ToString());
            var description = dialog.Text("Description", original.Get("description")); description.AcceptsReturn = true; description.TextWrapping = TextWrapping.Wrap; description.Height = 90; description.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            dialog.Note("Saving changes the VM in Proxmox. CPU/RAM changes may require a restart. The application will not restart the VM automatically.");
            dialog.Tab("Network");
            dialog.Note("Network interfaces — virtual cable state, not a guest connectivity test. Current shows the active configuration; editable values include pending changes.");
            var networkEditors=new List<(string Key,VmNetwork Original,ComboBox Bridge,CheckBox Connected)>();
            foreach(var key in original.Values.Keys.Concat(active.Values.Keys).Where(VmNetwork.IsKey).Distinct().OrderBy(k=>k.Length).ThenBy(k=>k)) {
                var activeRaw=active.Get(key);
                dialog.Note(key + " · Current: " + (activeRaw=="" ? "Not present" : new VmNetwork(activeRaw).Summary));
                var raw=original.Get(key);
                if(raw=="") { dialog.Note("Removal pending; edit this interface in Proxmox.");continue; }
                var network=new VmNetwork(raw);
                dialog.Note(network.Details);
                var bridge=AddBridgeChoice(dialog,key,network.Bridge,bridges);
                var connected=dialog.Check(key+" · Virtual cable connected",network.Connected);
                bridge.IsEnabled=connected.IsEnabled=canEditNetwork;
                networkEditors.Add((key,network,bridge,connected));
            }
            if(networkEditors.Count==0)dialog.Note("No editable network interfaces configured.");
            else dialog.Help(bridgesAvailable?(bridges.Count>0?$"Bridge choices from node {current.Node}. Only networks available to this VM and connection are listed. Missing networks? Open Options > Manage servers > Repair permissions, then reopen this window. The current network remains available in the list.":"Proxmox did not return any available networks. The list shows the current network only. Try Options > Manage servers > Repair permissions, then reopen this window."):"The network list could not be loaded. Check the server connection and permissions. The current value is kept, and you can still enter a network name.");
            if(!canEditNetwork)dialog.Note("Network editing is read-only: VM.Config.Network is missing or could not be verified.");
            dialog.Note("Changing the bridge or cable state can interrupt guest networking. Some changes may remain pending until a restart. Bridge/VNet access may also require SDN.Use.");
            var readLocal = AddConsoleSettings(dialog, saved);
            SavedVm? local = null;
            Dictionary<string, string>? changes = null;
            dialog.Validate = () =>
            {
                if (!int.TryParse(sockets.Text, out var s) || !int.TryParse(cores.Text, out var c) || !int.TryParse(ram.Text, out var m)) throw new ArgumentException("Enter CPU and RAM values as whole numbers.");
                local = readLocal();
                changes = original.Changes(name.Text.Trim(), description.Text, c, s, m);
                if(canEditNetwork)foreach(var editor in networkEditors) {
                    var value=editor.Original.Change(editor.Bridge.Text,editor.Connected.IsChecked==true);
                    if(value!=editor.Original.Raw)changes[editor.Key]=value;
                }
                return true;
            };
            if (dialog.ShowDialog() != true || changes == null || local == null) return;
            if (changes.Count == 0) { SaveConsoleSettings(saved, local); Status("VM settings saved locally."); return; }
            ct.ThrowIfCancellationRequested();
            // Resolve again after the dialog; the digest prevents overwriting concurrent edits.
            current = await api.ResolveVmAsync(saved.VmId, ct);
            Status("Saving configuration …"); var job = await api.UpdateConfigurationAsync(current, original.Digest, changes, ct); await WaitForJob(api, job, ct);
            current = await api.ResolveVmAsync(saved.VmId, ct); var readback = await api.GetConfigurationAsync(current, ct);
            if (changes.Any(p => VmNetwork.IsKey(p.Key) ? !VmNetwork.Equivalent(readback.Get(p.Key),p.Value) : readback.Get(p.Key) != p.Value)) throw new InvalidOperationException("Save task finished, but readback does not match. Check the configuration in Proxmox.");
            SaveConsoleSettings(saved, local);
            Status("Configuration saved and read back. CPU/RAM/network changes may still be pending; restart the VM if required.");
        });
        await PollStatus();
    }
}
















