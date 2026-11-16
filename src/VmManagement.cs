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
    readonly HashSet<Guid> stoppedConsoles=new();
    void ObservePowerState(Guid id,string? state)
    {
        if(state=="stopped" && consoles.ContainsKey(id))stoppedConsoles.Add(id);
    }
    async Task RestoreStartedConsole()
    {
        if(busy || closing || targetVm is not SavedVm vm || !stoppedConsoles.Contains(vm.Id))return;
        if(!runtime.TryGetValue(vm.Id,out var entry) || !entry.Fresh || entry.Label!="Running")return;
        stoppedConsoles.Remove(vm.Id);
        await ConnectVm(vm,fresh:true);
    }
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
            {
                runtime[selected.Id] = new(state, DateTimeOffset.UtcNow, null);
                ObservePowerState(selected.Id,state.Status);
            }
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
                        ObservePowerState(saved.Id,vm?.Status);
                    }
                    // Publish the cheap bulk status before permissions/configuration requests.
                    Filter(); UpdateRuntimeHeader();
                    var consoleRefresh=RefreshConsoleInfo(api,server,all);
                    try { if(!smokeMode)await SyncInventory(api,server,all); }
                    finally { await consoleRefresh; }
                }
                catch(Exception error) {
                    if(!closing && data.Servers.Contains(server)){
                        foreach(var v in favorites.Where(v=>data.Vms.Contains(v)&&v.Id!=targetVm?.Id))runtime[v.Id]=new(null,DateTimeOffset.UtcNow,"Unreachable");
                        if(!server.IgnoreCertificateErrors && TlsPolicy.Rejection(error) is {} rejected)InventoryCertificateFailure(server,error,rejected);
                    }
                }
            }
        }
        finally { await targetRefresh; polling = false; if (!closing) { Filter(); UpdateRuntimeHeader(); } }
        await RestoreStartedConsole();
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
            ObservePowerState(saved.Id,state.Status);
            if (action == "start")
            {
                if(consoles.ContainsKey(saved.Id))stoppedConsoles.Add(saved.Id);
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

            bool canEditNetwork=false,canEditHardware=false,canEditOptions=false,canEditMemory=false,canDisk=false,canCdrom=false,canPower=false,canStartup=false;
            try { (canEditNetwork,canEditHardware,canEditOptions,canEditMemory,canDisk,canCdrom,canPower)=await api.GetDevicePermissionsAsync(saved.VmId,ct);
                canStartup=canEditOptions && await api.CanEditStartupAsync(ct); }
            catch(System.Net.Http.HttpRequestException) { }
            IReadOnlyList<string> bridges=Array.Empty<string>();bool bridgesAvailable=false;
            if(canEditNetwork || original.Values.Keys.Any(VmNetwork.IsKey)) {
                using var bridgeTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);bridgeTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                try {bridges=await api.GetBridgesAsync(current.Node,bridgeTimeout.Token);bridgesAvailable=true;}
                catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
                catch { }
            }
            var dialog = new EditorDialog(this, $"VM settings · {saved.Name} · {server.Name}", "OK");
            dialog.Tab("General");
            dialog.Note("Server configuration includes pending changes. The local display name is independent.");
            var name = dialog.Text("VM name in Proxmox", original.Get("name"));
            dialog.Help("Set the VM name shown in Proxmox. Use the Console tab to choose a separate display name in DALIZO.");
            var description = dialog.Text("Description", original.Get("description")); description.AcceptsReturn = true; description.TextWrapping = TextWrapping.Wrap; description.Height = 90; description.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            dialog.Help("Save notes about this VM in Proxmox, such as its purpose or owner.");
            var agent=dialog.Check("Enable QEMU Guest Agent",original.AgentEnabled);
            agent.IsEnabled=canEditOptions;
            dialog.Hint("Active VM configuration: "+(active.AgentEnabled?"enabled":"disabled")+(active.AgentEnabled!=original.AgentEnabled?" · Change pending":""));
            dialog.Help("Enable the guest-agent channel in Proxmox. Install and start the QEMU Guest Agent service inside the guest OS. Pending changes may require shutting down and starting the VM.");
            if(!canEditOptions)dialog.Hint("Editing this option requires VM.Config.Options.");
            dialog.Tab("Hardware");
            dialog.Note("VM protection: "+(original.Number("protection",0)==1?"Enabled":"Disabled"));
            var addHardware=new Button {Content="Add hardware ▾",HorizontalAlignment=HorizontalAlignment.Left,
                ToolTip="Choose a device to add to the VM. Select OK to save the change."};
            var types=new ContextMenu {PlacementTarget=addHardware,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom};
            var addAudio=new MenuItem {Header="Audio device",ToolTip="Add an Intel HD Audio device for SPICE playback. Select OK to save it."};
            var addSerial=new MenuItem {Header="Serial port (socket)",ToolTip="Add the next available serial socket for a terminal connection. Select OK to save it."};
            types.Items.Add(addAudio);
            types.Items.Add(addSerial);
            var addNetwork=new MenuItem {Header="Network adapter",ToolTip="Choose an adapter model and the network to connect it to."};
            types.Items.Add(addNetwork);
            addHardware.ContextMenu=types;
            addHardware.Click+=(_,_)=>types.IsOpen=true;
            dialog.AddField(addHardware);
            var stagedNetworkRows=new StackPanel();
            dialog.AddField(stagedNetworkRows);
            var stagedNetworks=new Dictionary<string,string>();
            var removedNetworks=new HashSet<string>();
            dialog.Note("OK saves all your edits. Cancel discards them. Some hardware changes take effect after you shut down the VM and start it again.");
            if(!canEditHardware)dialog.Note("Audio and serial devices are read-only. Editing them requires VM.Config.HWType on this VM. For a DALIZO-managed account, open Options > Manage servers > Repair permissions.");

            string Cpu(VmConfiguration cfg)=>$"{cfg.Number("sockets",1)} socket(s), {cfg.Number("cores",1)} core(s) per socket";
            dialog.HardwareSection("Processors",Cpu(active),Cpu(original));
            var sockets = dialog.Text("CPU sockets", original.Number("sockets", 1).ToString());
            dialog.Help("Choose how many virtual processor sockets the guest sees. Together with cores per socket, this sets the virtual CPU topology.");
            var cores = dialog.Text("Cores per socket", original.Number("cores", 1).ToString());
            dialog.Help("Set the number of cores in each virtual socket. For example, two sockets with two cores each provide four virtual CPU cores.");
            dialog.HardwareSection("Memory",active.MemorySummary,original.MemorySummary);
            var ram = dialog.Text("RAM (MiB)", original.MemoryMiB.ToString());
            dialog.Help("Enter the configured memory in MiB (1024 MiB = 1 GiB). The value must be at least the configured ballooning minimum.");
            var balloon=dialog.Check("Enable memory ballooning",original.BalloonEnabled);
            dialog.Help("Ballooning lets Proxmox reclaim RAM from a running VM and return it later. When the host needs memory, it asks the balloon driver inside the guest to reserve some of the guest's memory pages. The host can then reuse the physical RAM behind those pages. When the balloon shrinks, the guest can use those pages again. This requires a working balloon driver in the guest.");
            var minimum=dialog.Text("Minimum memory (MiB)",original.Number("balloon",-1)>0?original.Get("balloon"):"");
            dialog.Help("Set the lowest memory target Proxmox can request. For example, with 8192 MiB of RAM and a 4096 MiB minimum, Proxmox can ask the guest to release up to 4096 MiB. Choose a minimum that can support the guest's workload. Leave the field blank to use the configured RAM as the minimum.");
            ram.IsEnabled=balloon.IsEnabled=canEditMemory;
            minimum.IsEnabled=canEditMemory && original.BalloonEnabled;
            balloon.Checked+=(_,_)=>minimum.IsEnabled=canEditMemory;
            balloon.Unchecked+=(_,_)=>minimum.IsEnabled=false;
            if(!canEditMemory)dialog.Hint("Editing memory requires VM.Config.Memory.");
            var networkEditors=new List<(string Key,VmNetwork Original,Func<string> Read)>();
            foreach(var key in original.Values.Keys.Concat(active.Values.Keys).Where(VmNetwork.IsKey).Distinct().OrderBy(k=>k.Length).ThenBy(k=>k)) {
                var activeRaw=active.Get(key);

                var raw=original.Get(key);
                var networkSection=dialog.HardwareSection("Network adapter · "+key,activeRaw==""?"Absent":new VmNetwork(activeRaw).Summary,raw==""?"Absent":new VmNetwork(raw).Summary);
                if(raw=="") { dialog.Note("Removal pending; edit this interface in Proxmox.");continue; }
                var network=new VmNetwork(raw);
                var body=(StackPanel)networkSection.Content;
                var firstField=body.Children.Count;
                var read=NetworkFields(dialog,key,network,bridges);
                var controls=body.Children.Cast<UIElement>().Skip(firstField).ToArray();
                foreach(var control in controls)control.IsEnabled=canEditNetwork;
                if(!bridgesAvailable)dialog.Hint("Network list unavailable. You can enter a bridge name.");
                var removeNic=new Button {Content="×",ToolTip="Remove "+key,IsEnabled=canEditNetwork,HorizontalAlignment=HorizontalAlignment.Right};
                SetDeviceAction(removeNic,"Remove "+key);
                PutDeviceAction(networkSection,removeNic);
                removeNic.Click+=(_,_)=>{
                    bool remove=removedNetworks.Add(key);
                    if(!remove)removedNetworks.Remove(key);
                    SetDeviceAction(removeNic,remove?"Undo removal of "+key:"Remove "+key,remove);
                    foreach(var control in controls)control.IsEnabled=!remove;
                };
                if(!canEditNetwork)dialog.Hint("Editing requires VM.Config.Network.");
                networkEditors.Add((key,network,read));
            }
            var hardwareEditors=new List<(string Key,string Value,GroupBox Section,bool Supported,Button Action)>();
            var desiredDevices=new HashSet<string>(original.Values.Keys);
            bool Available(string key)=>!desiredDevices.Contains(key) && original.Get(key)=="" && active.Get(key)=="";
            string? FreeNetworkSlot()=>Enumerable.Range(0,32).Select(i=>"net"+i).FirstOrDefault(k=>Available(k) && !stagedNetworks.ContainsKey(k));
            void RefreshDeviceTypes(){
                addAudio.IsEnabled=canEditHardware && Available("audio0");
                addSerial.IsEnabled=canEditHardware && hardwareEditors.Any(d=>VmConfiguration.IsSerialSocketSlot(d.Key) && Available(d.Key));
                addNetwork.IsEnabled=canEditNetwork && FreeNetworkSlot()!=null;
                addHardware.IsEnabled=canDisk || canCdrom || canEditHardware || addNetwork.IsEnabled;
            }
            addNetwork.Click+=(_,_)=>{
                if(!canEditNetwork || FreeNetworkSlot() is not string key)return;
                var picker=new EditorDialog(dialog,"Add network adapter · "+key,"Add");
                var read=NetworkFields(picker,key,new VmNetwork("virtio,firewall=1"),bridges);
                string config="";
                picker.Validate=()=>{config=read();return true;};
                if(picker.ShowDialog()!=true)return;
                stagedNetworks.Add(key,config);
                var row=new DockPanel {Margin=new Thickness(0,4,0,4)};
                var cancel=new Button {Content="×",ToolTip="Cancel addition of "+key};
                SetDeviceAction(cancel,"Cancel addition of "+key,true);
                DockPanel.SetDock(cancel,Dock.Right);row.Children.Add(cancel);
                row.Children.Add(new TextBlock {Text=$"{key} · {new VmNetwork(config).Model} · {new VmNetwork(config).Summary} · To be added",TextWrapping=TextWrapping.Wrap});
                stagedNetworkRows.Children.Add(row);
                cancel.Click+=(_,_)=>{stagedNetworks.Remove(key);stagedNetworkRows.Children.Remove(row);RefreshDeviceTypes();};
                RefreshDeviceTypes();
            };
            void AddDevice(string key,string label,string value){
                string configured=original.Get(key), running=active.Get(key);
                var section=dialog.HardwareSection(label,running==""?"Absent":running,configured==""?"Absent":configured);
                section.Visibility=running.Length>0 || configured.Length>0 ? Visibility.Visible : Visibility.Collapsed;
                bool supported=key=="audio0" || (configured is "" or "socket" && running is "" or "socket");
                var action=new Button {Width=30,Height=28,Padding=new Thickness(0),FontSize=18,
                    HorizontalAlignment=HorizontalAlignment.Right,IsEnabled=canEditHardware,
                    Visibility=supported && (configured.Length>0 || running.Length==0) ? Visibility.Visible : Visibility.Collapsed};
                SetDeviceAction(action,"Remove "+label);
                PutDeviceAction(section,action);
                action.Click+=(_,_)=>{
                    if(desiredDevices.Remove(key)){
                        if(configured.Length==0)section.Visibility=Visibility.Collapsed;
                        else {SetDeviceAction(action,"Undo removal of "+label,true);section.Header=label+" · To be removed";}
                    }else{desiredDevices.Add(key);SetDeviceAction(action,"Remove "+label);section.Header=label;}
                    RefreshDeviceTypes();
                };
                if(!supported)dialog.Hint("Host serial devices are managed in Proxmox.");
                hardwareEditors.Add((key,value,section,supported,action));
            }
            AddDevice("audio0","Audio device (audio0)",VmConfiguration.SpiceAudio);
            dialog.Help("New audio devices use Intel HD Audio (ICH9) with the SPICE backend.");
            for(int i=0;i<4;i++)AddDevice("serial"+i,$"Serial port {i} (socket)","socket");
            void StageDevice(string key){
                if(!canEditHardware || !Available(key))return;
                desiredDevices.Add(key);
                var editor=hardwareEditors.First(d=>d.Key==key);
                var section=editor.Section;
                SetDeviceAction(editor.Action,"Cancel addition of "+key,true);
                section.Header=key=="audio0"?"Audio device (audio0) · To be added":$"Serial port ({key}) · To be added";
                section.Visibility=Visibility.Visible;
                section.BringIntoView();
                RefreshDeviceTypes();
            }
            addAudio.Click+=(_,_)=>StageDevice("audio0");
            addSerial.Click+=(_,_)=>{
                var free=hardwareEditors.FirstOrDefault(d=>VmConfiguration.IsSerialSocketSlot(d.Key) && Available(d.Key));
                if(free.Key!=null)StageDevice(free.Key);
            };
            RefreshDeviceTypes();
            var driveChanges=await AddDriveSettings(dialog,types,original,active,current,api,canDisk,canCdrom,canEditHardware,ct);

            var enhancements=original.Get("spice_enhancements").Split(',',StringSplitOptions.RemoveEmptyEntries);
            var streaming=enhancements.FirstOrDefault(p=>p.StartsWith("videostreaming="))?.Split('=')[1]??"off";
            ComboBox streamBox=null!;
            var readLocal=AddConsoleSettings(dialog,saved,()=>{
            streamBox=dialog.Choice("Video streaming (Proxmox VM)",new[]{"off","filter","all"}.Append(streaming).Distinct().Cast<object>().ToArray(),streaming);
            streamBox.DisplayMemberPath="";
            dialog.Help("Choose the VM's SPICE streaming policy. Filter detects video regions. All requests streaming for all regions. Off disables video streaming. The server checks write access to spice_enhancements and may require root.",false);
            });
            var readOptions=AddVmOptions(dialog,original,canEditOptions,canEditHardware,canDisk,canPower,canStartup);
            SavedVm? local = null;
            Dictionary<string, string>? changes = null;
            dialog.Validate = () =>
            {
                if (!int.TryParse(sockets.Text, out var s) || !int.TryParse(cores.Text, out var c) || !int.TryParse(ram.Text, out var m)) throw new ArgumentException("Enter CPU and RAM values as whole numbers.");
                local = readLocal();
                int requestedMinimum=balloon.IsChecked==true?-1:0;
                if(requestedMinimum==-1 && !string.IsNullOrWhiteSpace(minimum.Text) &&
                    (!int.TryParse(minimum.Text,out requestedMinimum) || requestedMinimum<1))
                    throw new ArgumentException("Enter a positive minimum in MiB or leave the field blank.");
                changes = original.Changes(name.Text.Trim(), description.Text, c, s, m,requestedMinimum);
                bool enableAgent=agent.IsChecked==true;
                if(canEditOptions && enableAgent!=original.AgentEnabled)changes["agent"]=original.AgentValue(enableAgent);
                var removed=changes.TryGetValue("delete",out var memoryDeletes)?memoryDeletes.Split(',').ToList():new List<string>();
                readOptions(changes,removed);
                if((string)streamBox.SelectedItem!=streaming)
                    changes["spice_enhancements"]=string.Join(",",enhancements.Where(p=>!p.StartsWith("videostreaming=")).Append("videostreaming="+streamBox.SelectedItem));
                foreach(var (key,value,_,supported,_) in hardwareEditors){
                    if(!canEditHardware || !supported || desiredDevices.Contains(key)==original.Values.ContainsKey(key))continue;
                    if(desiredDevices.Contains(key))changes[key]=value;else removed.Add(key);
                }
                removed.AddRange(removedNetworks);
                foreach(var (key,value) in driveChanges){
                    if(value==null)removed.Add(key);else changes[key]=value;
                }
                if(removed.Count>0)changes["delete"]=string.Join(",",removed);
                if(changes.TryGetValue("boot",out var order) && order[6..].Split(';').Intersect(removed).Any())
                    throw new ArgumentException("Remove detached devices from the boot order.");
                foreach(var (key,value) in stagedNetworks)changes[key]=value;
                if(canEditNetwork)foreach(var editor in networkEditors) {
                    if(removedNetworks.Contains(editor.Key))continue;
                    var value=editor.Read();
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
            if (changes.Any(p => !readback.Matches(p.Key,p.Value))) throw new InvalidOperationException("Save task finished, but readback does not match. Check the configuration in Proxmox.");
            SaveConsoleSettings(saved, local);
            Status("Configuration saved and read back. Check pending hardware changes before reconnecting.");
        });
        await PollStatus();
    }
}
















