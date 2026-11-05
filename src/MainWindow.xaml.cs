// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace ProxmoxSpice;

public partial class MainWindow : Window
{
    readonly ProfileStore store;
    WorkspaceData data;
    readonly Dictionary<Guid, string> sessionSecrets = new();
    CancellationTokenSource? operation;
    SpiceHost? host;
    NoVncHost? vncHost;
    bool busy, closing;
    Func<ServerProfile, string, ProxmoxApi> apiFactory = (s, secret) => { var api = new ProxmoxApi(s.Address); api.UseToken(s.TokenId, secret); return api; };
    public MainWindow(ProfileStore store, bool smoke = false, string? liveVm = null)
    {
        this.store = store; data = store.Load(); InitializeComponent(); InitializeVmSort(); InitializeGuestShortcuts(); InitializeStatusNotification(); InitializeFloatingSidebar(); InitializeManagement(smoke || liveVm != null); Refresh(); ShowConsoleHome();
        StatusText.Text = data.Servers.Count == 0
            ? "Ready · Add a server profile to begin."
            : "Ready · Open the VM menu to select a VM and connect.";

        Closed += (_, _) => { closing = true; operation?.Cancel(); CloseAllConsoles(); host?.Dispose(); vncHost?.Dispose(); sessionSecrets.Clear(); };
        if (liveVm != null) Loaded += async (_, _) => await LiveRenderTest(liveVm);
        else if (smoke) Loaded += async (_, _) => await SmokeTest();
    }
    // WPF tracks selected items in hash-based collections. Mutable UI state must
    // not affect equality or hashing, so rows use reference identity, not records.
    sealed class VmRow(Guid id, string name, int vmId, string serverName, string runtimeStatus, string consoleType, string folder = "") : System.ComponentModel.INotifyPropertyChanged
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public string Folder {get;}=folder;
        public string FolderGroup => Folder.Length==0 ? "Ungrouped" : Folder;
        public int VmId { get; } = vmId;
        public string ServerName { get; } = serverName;
        public string RuntimeStatus { get; private set; } = runtimeStatus;
        public string ConsoleType { get; private set; } = consoleType;
        public void UpdateObserved(string status,string console) {
            if(RuntimeStatus!=status) {
                RuntimeStatus=status;
                foreach(var property in new[]{nameof(RuntimeStatus),nameof(StatusSymbol),nameof(StatusBrush)})
                    PropertyChanged?.Invoke(this,new System.ComponentModel.PropertyChangedEventArgs(property));
            }
            if(ConsoleType!=console) {ConsoleType=console;PropertyChanged?.Invoke(this,new System.ComponentModel.PropertyChangedEventArgs(nameof(ConsoleType)));}
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        string sessionState="";
        public string SessionState => sessionState;
        public string ConnectHint => sessionState=="Console open" ? "Show console" : "Connect";
        public Visibility SessionVisibility => sessionState=="" ? Visibility.Collapsed : Visibility.Visible;
        public Brush SessionBrush => sessionState=="Console open" ? Brushes.DodgerBlue : Brushes.SlateGray;
        public bool CanStart {get;private set;}
        public bool CanShutdown {get;private set;}
        public string StartHint {get;private set;}="Wait for VM status.";
        public string ShutdownHint {get;private set;}="Wait for VM status.";
        public string RestartHint => CanShutdown?"Restart VM (confirmation required)":ShutdownHint.Replace("Shutdown","Restart");
        public void SetPowerActions(bool start,bool shutdown,string startHint,string shutdownHint){
            if(CanStart==start&&CanShutdown==shutdown&&StartHint==startHint&&ShutdownHint==shutdownHint)return;
            CanStart=start;CanShutdown=shutdown;StartHint=startHint;ShutdownHint=shutdownHint;
            foreach(var property in new[]{nameof(CanStart),nameof(CanShutdown),nameof(StartHint),nameof(ShutdownHint),nameof(RestartHint)})PropertyChanged?.Invoke(this,new System.ComponentModel.PropertyChangedEventArgs(property));
        }
        public bool CanConnect { get; private set; }
        public bool CanDisconnect { get; private set; }
        public void SetActions(bool canConnect, bool canDisconnect) {
            if(CanConnect==canConnect && CanDisconnect==canDisconnect)return;
            CanConnect=canConnect; CanDisconnect=canDisconnect;
            PropertyChanged?.Invoke(this,new System.ComponentModel.PropertyChangedEventArgs(nameof(CanConnect)));
            PropertyChanged?.Invoke(this,new System.ComponentModel.PropertyChangedEventArgs(nameof(CanDisconnect)));
        }
        public void SetSessionState(string value) {
            if(sessionState==value)return;sessionState=value;
            foreach(var property in new[]{nameof(SessionState),nameof(SessionVisibility),nameof(SessionBrush),nameof(ConnectHint)})
                PropertyChanged?.Invoke(this,new System.ComponentModel.PropertyChangedEventArgs(property));
        }
        public string StatusSymbol => StatusSymbolFor(RuntimeStatus);
        public Brush StatusBrush => StatusBrushFor(RuntimeStatus);
    }
    SavedVm? SelectedVm => VmList.SelectedItem is VmRow row ? data.Vms.FirstOrDefault(v => v.Id == row.Id) : null;
    void Status(string text) { if (!closing) { bool changed=StatusText.Text!=text; StatusText.Text=text; if(changed)ShowStatusNotification(); } }
    void Refresh(Guid? selectedVm = null, Guid? selectedServer = null)
    {
        ServerColumn.Visibility=data.Servers.Count>1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateVmColumnWidths();
        selectedServer ??= (Servers.SelectedItem as ServerProfile)?.Id;
        Servers.ItemsSource = data.Servers.OrderBy(s => s.Name).ToArray();
        Servers.SelectedItem = data.Servers.FirstOrDefault(s => s.Id == selectedServer) ?? data.Servers.FirstOrDefault();
        Filter(selectedVm); RefreshConsoleSessions();
        if (targetVm != null && !data.Vms.Contains(targetVm)) SetTarget(data.Vms.FirstOrDefault(v => v.Id == targetVm.Id));
    }
    void Filter(Guid? selected = null)
    {
        if (VmList == null) return;
        selected ??= (VmList.SelectedItem as VmRow)?.Id;
        var query = Search.Text.Trim();
        var servers=data.Servers.ToDictionary(s=>s.Id,s=>s.Name);
        var validIds=data.Vms.Select(v=>v.Id).ToHashSet();
        foreach(var removed in vmRows.Keys.Where(id=>!validIds.Contains(id)).ToArray())vmRows.Remove(removed);
        var rows = data.Vms.Where(v=>!templates.Contains(v.Id)&&!inventoryHidden.Contains(v.Id)).Select(v => {
            var serverName=servers[v.ServerId];
            if(!vmRows.TryGetValue(v.Id,out var row) || row.Name!=v.Name || row.VmId!=v.VmId || row.ServerName!=serverName || row.Folder!=v.Folder)
                vmRows[v.Id]=row=new VmRow(v.Id,v.Name,v.VmId,serverName,CachedStatus(v.Id),ConsoleLabel(v),v.Folder);
            else row.UpdateObserved(CachedStatus(v.Id),ConsoleLabel(v));
            return row;
        }).Where(r => ($"{r.Name} {r.VmId} {r.ServerName} {r.Folder}").Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        refreshingRows = true;
        try {
            // Keep the collection view (and its sorted rows) during status-only updates.
            if(VmList.ItemsSource is not VmRow[] previous || !previous.SequenceEqual(rows)) {
                VmList.ItemsSource=rows;
                ApplyVmSort();
            }
            var selection=rows.FirstOrDefault(v=>v.Id==selected);
            if(!ReferenceEquals(VmList.SelectedItem,selection))VmList.SelectedItem=selection;
        } finally { refreshingRows=false; }
        UpdateSessionIndicators(); UpdateConnectButtons();
        CountLabel.Text = $"{data.Vms.Count(v=>!templates.Contains(v.Id)&&!inventoryHidden.Contains(v.Id))} VMs · {rows.Length} shown";
    }
    readonly Dictionary<Guid,VmRow> vmRows=new();
    void FilterChanged(object sender, TextChangedEventArgs e) => Filter();
    void Commit(WorkspaceData next, Guid? vm = null, Guid? server = null) { store.Save(next); data = next; Refresh(vm, server); }
    WorkspaceData Snapshot() => new(1, data.Servers.ToList(), data.Vms.ToList());
    void Guard(Action action) { try { action(); } catch (Exception e) { Status("Action failed: " + e.Message); } }
    void OptionsOpened(object sender,RoutedEventArgs e) { guestKeysMenu.IsOpen=false; ShowSidebar(false); HideStatusNotification(); ManagementToggle.IsChecked=false; }
    void EditServer(object sender, RoutedEventArgs e) => Guard(() => { if (Servers.SelectedItem is ServerProfile s) EditServerProfile(s); });
    void EditServerProfile(ServerProfile? original,Window? owner=null)
    {
        var dialog = new EditorDialog(owner ?? this, original == null ? "Add server manually" : "Edit server");
        var name = dialog.Text("Display name", original?.Name ?? "");
        dialog.Note("Example: Home lab or Production cluster.");
        var address = dialog.Text("Proxmox address (HTTPS)", original?.Address ?? "");
        dialog.Note("Example format: https://pve.example.org:8006 — replace with your actual server address.");
        var token = dialog.Text("Token ID (user@realm!tokenname)", original?.TokenId ?? "");
        dialog.Note("Copy the full Token ID from Proxmox. Example: dalizo@pve!windows-client. A username alone is not enough.");
        var secretValue = original != null && sessionSecrets.TryGetValue(original.Id, out var cached) ? cached : "";
        if (secretValue == "" && original?.ProtectedSecret != null) { try { secretValue = SecretProtection.Unprotect(original.ProtectedSecret); } catch { dialog.Note("The saved secret cannot be read. Please enter it again."); } }
        var secret = dialog.Password("Token secret", secretValue);
        dialog.Note("Paste the secret shown when the API token was created. This is the application’s login key, not your Proxmox password.");
        var remember = dialog.Check("Remember this connection on this Windows account", original?.ProtectedSecret != null);
        var proxy = dialog.Text("SPICE proxy (optional, without port)", original?.Proxy ?? "");
        dialog.Note("Example format: pve.example.org. Leave empty to use the server hostname.");
        dialog.Note("The saved login key is encrypted for your Windows account. If you do not save it, you will need it again after closing DALIZO. The server must have a trusted HTTPS certificate.");
        AddConnectionTest(dialog, address, token, secret, proxy);
        if(original!=null)AddAdministratorSetup(dialog,address,token,secret,remember);
        ServerProfile? result = null; string plain = "";
        dialog.Validate = () =>
        {
            plain = secret.Password;
            if (remember.IsChecked == true && string.IsNullOrEmpty(plain)) throw new InvalidOperationException("Enter a token secret to save it.");
            result = new(original?.Id ?? Guid.NewGuid(), name.Text.Trim(), address.Text.Trim(), token.Text.Trim(), proxy.Text.Trim(), remember.IsChecked == true ? SecretProtection.Protect(plain) : null);
            var next = Snapshot(); next.Servers.RemoveAll(s => s.Id == result.Id); next.Servers.Add(result); ProfileStore.Validate(next); return true;
        };
        if (dialog.ShowDialog() != true || result == null) return;
        var updated = Snapshot(); updated.Servers.RemoveAll(s => s.Id == result.Id); updated.Servers.Add(result); Commit(updated, server: result.Id);
        if (plain != "") sessionSecrets[result.Id] = plain; else sessionSecrets.Remove(result.Id);
        lastInventorySync.Remove(result.Id); Status("Server profile saved. The VM list will synchronize automatically."); if(!smokeMode)_=PollStatus();
    }
    void AddVm(object sender, RoutedEventArgs e) => Guard(() => EditVmProfile(null));
    void EditVm(object sender, RoutedEventArgs e) => Guard(() => { if (HasDisplayedConsole && !busy && targetVm is SavedVm vm) EditVmProfile(vm); });
    void EditVmProfile(SavedVm? original)
    {
        if (data.Servers.Count == 0) { Status("Add a server profile first."); return; }
        var dialog = new EditorDialog(this, original == null ? "Add VM" : "VM properties");
        var name = dialog.Text("Custom display name", original?.Name ?? "");
        var server = dialog.Choice("Server profile", data.Servers.Cast<object>().ToArray(), data.Servers.FirstOrDefault(s => s.Id == original?.ServerId) ?? Servers.SelectedItem);
        var id = dialog.Text("Proxmox-VM-ID", original?.VmId.ToString() ?? "");
        var consoleType = dialog.Choice("Console", new object[] { "Auto", "SPICE", "noVNC", "Serial terminal" }, original?.ConsoleType ?? "Auto");
        consoleType.DisplayMemberPath=""; dialog.Note("Auto checks the current Proxmox display: QXL selects SPICE; otherwise noVNC. Manual choices are checked before connecting.");
        dialog.Note("The current cluster node is resolved through the API for each connection. This creates a local entry only; the VM itself is unchanged.");
        var clipboard = dialog.Check("Share clipboard with this VM", original?.ClipboardEnabled ?? true);
        var resize = dialog.Check("Auto resolution", original?.AutoResolution ?? true);
        dialog.Note("Auto resolution requires SPICE guest tools and a compatible graphics driver. noVNC requests guest resizing when supported by the server and scales the image as a fallback. Clipboard sharing requires guest support; noVNC uses Send text to VM in the top VM actions menu.");
        SavedVm? result = null;
        dialog.Validate = () => { if (server.SelectedItem is not ServerProfile s || !int.TryParse(id.Text, out var vmid) || vmid <= 0) throw new InvalidOperationException("Select a server and enter a positive VM ID."); result = new(original?.Id ?? Guid.NewGuid(), s.Id, vmid, name.Text.Trim(), (string)consoleType.SelectedItem, true, clipboard.IsChecked==true, resize.IsChecked==true,original?.SerialPort??"serial0",original?.Folder??"",original?.ImageClipboardEnabled??false,original?.FileTransferEnabled??false); var next = Snapshot(); next.Vms.RemoveAll(v => v.Id == result.Id); next.Vms.Add(result); ProfileStore.Validate(next); return true; };
        if (dialog.ShowDialog() != true || result == null) return;
        if(original!=null && (original.ServerId!=result.ServerId || original.VmId!=result.VmId || original.ConsoleType!=result.ConsoleType))CloseConsole(original.Id);
        var updated = Snapshot(); updated.Vms.RemoveAll(v => v.Id == result.Id); updated.Vms.Add(result); Commit(updated, result.Id); Status("VM saved. Click Connect to connect.");
    }
    void RemoveVm(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (SelectedVm is not SavedVm vm) return;
        if (!EditorDialog.Confirm(this, $"Remove “{vm.Name}” from the local list? The VM in Proxmox will remain unchanged.", "Remove entry")) return;
        var next = Snapshot(); next.Vms.RemoveAll(v => v.Id == vm.Id); Commit(next); Status("Local VM entry removed.");
    });
    string? GetSecret(ServerProfile server)
    {
        if (sessionSecrets.TryGetValue(server.Id, out var cached)) return cached;
        if (server.ProtectedSecret != null) { try { var decrypted = SecretProtection.Unprotect(server.ProtectedSecret); sessionSecrets[server.Id] = decrypted; return decrypted; } catch { Status("The saved secret cannot be read. Please enter it again."); } }
        var dialog = new EditorDialog(this, "API token for " + server.Name); var password = dialog.Password("Token secret");
        dialog.Note("The secret is kept for this session only. To save it permanently, use Edit server.");
        dialog.Validate = () => { if (string.IsNullOrWhiteSpace(password.Password)) throw new InvalidOperationException("Enter the token secret."); return true; };
        if (dialog.ShowDialog() != true) return null;
        sessionSecrets[server.Id] = password.Password; return password.Password;
    }
    async Task RunOperation(Func<CancellationToken, Task> action)
    {
        if (busy || closing) return;
        busy = true; OptionsToggle.IsEnabled = ServerActions.IsEnabled = VmList.IsEnabled = false; UpdateRuntimeHeader();
        var cts = new CancellationTokenSource(); operation = cts;
        try { await action(cts.Token); }
        catch (OperationCanceledException) { SetConsoleFocus(false); Status("Request cancelled or timed out. Server actions already submitted may continue."); }
        catch (Exception e) { SetConsoleFocus(false); Status(e is HttpRequestException ? "API connection failed. Check the address, HTTPS certificate and token permissions. " + (e.InnerException == null ? e.Message : "") : "Connection failed: " + e.Message); }
        finally { if (operation == cts) operation = null; cts.Dispose(); busy = false; if (!closing) OptionsToggle.IsEnabled = ServerActions.IsEnabled = VmList.IsEnabled = true; UpdateRuntimeHeader(); }
    }
    async void VmKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await ConnectSelected(); } }
    async void VmDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left || busy || closing)return;
        var element=e.OriginalSource as DependencyObject;
        while(element!=null && element is not DataGridRow) {
            // Row action buttons must never also trigger a connection.
            if(element is System.Windows.Controls.Primitives.ButtonBase)return;
            element=element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        if(element is not DataGridRow { Item: VmRow row } || ItemsControl.ItemsControlFromItemContainer(element)!=VmList)return;
        e.Handled=true;
        VmList.SelectedItem=row;
        await ConnectSelected();
    }
    async void RowPropertiesClicked(object sender, RoutedEventArgs e)
    {
        e.Handled=true;
        if(busy || closing || sender is not FrameworkElement { DataContext: VmRow row })return;
        if(data.Vms.FirstOrDefault(v=>v.Id==row.Id) is {} vm)await ConfigureVm(vm);
    }
    async void RowConnectClicked(object sender, RoutedEventArgs e)
    {
        e.Handled=true;
        if(busy || closing || sender is not FrameworkElement { DataContext: VmRow row } || !row.CanConnect)return;
        VmList.SelectedItem=row;
        await ConnectSelected();
    }
    void RowDisconnectClicked(object sender, RoutedEventArgs e)
    {
        e.Handled=true;
        if(busy || closing || sender is not FrameworkElement { DataContext: VmRow row } || !row.CanDisconnect)return;
        CloseConsoleFromUser(row.Id);
        Status($"Console closed for {row.Name}.");
    }
    void DisconnectClicked(object sender, RoutedEventArgs e) { if(!HasDisplayedConsole)return; operation?.Cancel(); CloseConsoleFromUser(targetVm!.Id); }
    void CtrlAltDeleteClicked(object sender, RoutedEventArgs e)
    {
        SendGuestShortcut(GuestShortcuts.All[0]);
    }
    async Task ConnectSelected()
    {
        if (SelectedVm is SavedVm saved) await ConnectVm(saved);
    }
    async Task ConnectVm(SavedVm saved, bool fresh = false)
    {
        if (busy || closing || !data.Vms.Contains(saved)) return;
        SetTarget(saved);
        if(!fresh && consoles.TryGetValue(saved.Id,out var existing) && existing.Open) { RestoreConsole(saved); ShowSidebar(false);return; }
        CloseConsole(saved.Id);
        var server = data.Servers.First(s => s.Id == saved.ServerId); var secret = GetSecret(server); if (secret == null) return;
        await RunOperation(async ct =>
        {
            SetTarget(saved); DisconnectConsole(); ConsoleTitle.Text = saved.Name; ConsoleSubtitle.Text = $"{server.Name} · VM {saved.VmId} · {ConsoleLabel(saved)}";
            Status("Resolving the VM and its current cluster node …");
            using var api = apiFactory(server, secret);
            var all = await api.ListVmsAsync(ct); ct.ThrowIfCancellationRequested();
            var current = all.FirstOrDefault(v => v.Id == saved.VmId) ?? throw new InvalidOperationException("VM ID not visible. Check the server profile, VM ID and permissions.");
            if (current.Status != "running") throw new InvalidOperationException("The VM is not running. Start it first, then reconnect.");
            var detected=await api.GetConsoleInfoAsync(current,ct);
            consoleInfo[saved.Id]=new(detected,DateTimeOffset.UtcNow);
            if(detected.IsTemplate) { templates.Add(saved.Id); SetTarget(null); Filter(); throw new InvalidOperationException("Templates cannot be connected and have been hidden."); }
            var consoleType=detected.Resolve(saved.ConsoleType); Filter(saved.Id);
            ConsoleSubtitle.Text=$"{server.Name} · VM {saved.VmId} · {consoleType}";
            if(consoleType is "noVNC" or "Serial terminal")
            {
                if(consoleType=="Serial terminal"&&!detected.SerialPorts.Contains(saved.SerialPort))throw new InvalidOperationException(saved.SerialPort+" is not configured as a socket. Select an available serial port in VM settings.");
                var candidate = new NoVncHost(consoleType=="Serial terminal"?saved.SerialPort:null); vncHost=candidate; 
                candidate.StatusChanged += text => { if(vncHost==candidate)Status(text); RefreshConsoleSessions(); };
                candidate.InputAvailabilityChanged += available => { if(vncHost!=candidate)return; ConsoleAvailabilityChanged(available); VncPasteButton.IsEnabled=available && ClipboardEnabled; };
                candidate.ReleaseFocus += () => { if(vncHost==candidate)SidebarButton.Focus(); };
                ConsoleArea.Child=candidate; VncPasteButton.Visibility=Visibility.Visible;
                candidate.SetClipboardSharing(ClipboardEnabled); candidate.SetResizeEnabled(AutoResolutionEnabled);
                try {
                    Status("Preparing "+consoleType+" …");
                    await candidate.InitializeAsync(ct);
                    Status("Requesting a fresh "+consoleType+" ticket …");
                    await candidate.ConnectAsync(api,current,ct); RememberConsole(saved);
                }
                catch { candidate.Dispose(); if(vncHost==candidate) { vncHost=null; ConsoleArea.Child=new TextBlock { Text=consoleType+" connection not established.",Margin=new Thickness(24) }; } throw; }
                return;
            }
            EnsureHost(); // Initialize the native runtime before consuming the short-lived ticket.
            Status("Requesting a fresh SPICE ticket …");
            var config = SpiceConfig.Parse(await api.GetConsoleAsync(current, string.IsNullOrWhiteSpace(server.Proxy) ? api.Host : server.Proxy, ct));
            ct.ThrowIfCancellationRequested(); host!.Connect(config); RememberConsole(saved);
        });
    }
    void DisconnectConsole()
    {
        if(targetVm!=null && consoles.ContainsKey(targetVm.Id)) { CloseConsole(targetVm.Id); return; }
        host?.Disconnect();
        if(vncHost!=null) { vncHost.Dispose(); vncHost=null; ConsoleArea.Child=new TextBlock { Text="Console disconnected. Click Connect to reconnect.",Margin=new Thickness(24) }; }
        if(VncPasteButton!=null) { VncPasteButton.Visibility=Visibility.Collapsed; VncPasteButton.IsEnabled=false; }
    }
    void VncPasteClicked(object sender,RoutedEventArgs e) => Guard(()=>{ManagementToggle.IsChecked=false;vncHost?.SendClipboard();});
    void EnsureHost()
    {
        
        VncPasteButton.Visibility=Visibility.Collapsed;
        if (host != null) { ConsoleArea.Child=host; host.SetExtraSharing(targetVm?.ImageClipboardEnabled==true,targetVm?.FileTransferEnabled==true);host.SetClipboardSharing(ClipboardEnabled); host.SetResizeEnabled(AutoResolutionEnabled); return; }
        var candidate = new SpiceHost(); candidate.StatusChanged += text => { if(host==candidate)Status(text); RefreshConsoleSessions(); };
        candidate.InputAvailabilityChanged += available => { if(host==candidate)ConsoleAvailabilityChanged(available); };
        candidate.ClipboardStateChanged += _ => { if(host==candidate)UpdateFeatureIndicators(); };
        candidate.SetExtraSharing(targetVm?.ImageClipboardEnabled==true,targetVm?.FileTransferEnabled==true);
        candidate.FilesDropped+=files=>{if(host==candidate&&targetVm is SavedVm vm)_=ShowFileTransfer(vm,candidate,files);};
        candidate.SetClipboardSharing(ClipboardEnabled); candidate.SetResizeEnabled(AutoResolutionEnabled);
        try { ConsoleArea.Child = candidate; UpdateLayout(); host = candidate; }
        catch { ConsoleArea.Child = new TextBlock { Text = "The SPICE runtime could not be embedded.", Margin = new Thickness(24) }; candidate.Dispose(); throw; }
    }
    async Task SmokeTest()
    {
        try
        {
            var server = new ServerProfile(Guid.NewGuid(), "Lab Proxmox", "https://pve.example.test:8006", "test@pve!console", "", SecretProtection.Protect("synthetic-secret"));
            var vm = new SavedVm(Guid.NewGuid(), server.Id, 101, "Windows Test-VM");
            Commit(new(1, new() { server }, new() { vm, new(Guid.NewGuid(), server.Id, 102, "Linux Build-Server") }), vm.Id, server.Id);
            if (store.Load().Vms.Count != 2 || SecretProtection.Unprotect(store.Load().Servers[0].ProtectedSecret!) != "synthetic-secret") throw new Exception("Profile reload / DPAPI failed");
            sessionSecrets[server.Id] = "synthetic-secret";
            if(Environment.GetCommandLineArgs().Contains("--vm-menu-test")) {
                CheckRichClipboard();await CheckUnifiedMenus();await CheckVmListSorting();await CheckRowPower();await CheckSetupDialogs();await CheckVmSettings(vm);await CheckVmFolders(server);CheckClipboardKeystrokes();
                using(var ct=new CancellationTokenSource(TimeSpan.FromSeconds(30))) {
                    using var testVnc=new NoVncHost();ConsoleArea.Child=testVnc;UpdateLayout();await testVnc.InitializeAsync(ct.Token);await testVnc.CheckSyntheticSessionAsync(ct.Token);
                }
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"smoke-result.txt"),"PASS: focused VM menu tests; sorting and folder grouping/persistence. SPICE rendering excluded from this focused run.");return;
            }
            if (DescribeFeature(false,true,true,true,true,false).State != "Not checked"
                || DescribeFeature(true,true,false,true,true,false).State != "Disabled"
                || DescribeFeature(true,true,true,false,false,false).State != "Agent unavailable"
                || DescribeFeature(true,true,true,true,false,false).State != "Not advertised"
                || DescribeFeature(true,true,true,true,true,false).State != "Supported"
                || DescribeFeature(true,false,true,false,false,false).State != "Not verified"
                || DescribeFeature(true,false,true,false,false,true).State != "Not verified")
                throw new Exception("Console feature availability states are incorrect");
            await CheckVmListSorting();
            await CheckSetupDialogs();
            await CheckVmSettings(vm);
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            // Deterministic failing proxy: accept and close instead of relying
            // on OS-specific retries for a non-listening port.
            socket.Listen(8);
            var releaseProxy=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(async () => { while(true) { try { using var peer=await socket.AcceptAsync(); await releaseProxy.Task; } catch(ObjectDisposedException){return;} catch(System.Net.Sockets.SocketException){return;} } });
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=probe", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var config = $"[virt-viewer]\ntype=spice\nhost=probe\ntls-port=1\npassword=synthetic-ticket\nproxy=http://127.0.0.1:{port}\nhost-subject=CN=probe\nca={cert.ExportCertificatePem().Replace("\r", "").Replace("\n", "\\n")}\n";
            var requests = new List<string>();
            apiFactory = (s, key) => { var api = new ProxmoxApi(s.Address, new SmokeHttp(req => { requests.Add(req.RequestUri!.AbsolutePath); return req.RequestUri.AbsolutePath.Contains("resources") ? "{\"data\":[{\"type\":\"qemu\",\"vmid\":101,\"node\":\"migrated-node\",\"name\":\"Guest\",\"status\":\"running\"}]}" : req.RequestUri.AbsolutePath.EndsWith("/config") ? "{\"data\":{\"vga\":\"qxl\"}}" : config; })); api.UseToken(s.TokenId, key); return api; };
            await CheckRowActionBindings(vm.Id);
            if(DisconnectButton.IsEnabled || VmPropertiesButton.IsEnabled || ManagementToggle.IsEnabled)throw new Exception("Toolbar disconnect enabled without a console.");
            await ConnectSelected();
            if(!DisconnectButton.IsEnabled || !VmPropertiesButton.IsEnabled || !ManagementToggle.IsEnabled)throw new Exception("Toolbar disconnect disabled for displayed console.");
            if (host == null || !host.HasDisplay) throw new Exception("SPICE decoder creation failed: " + StatusText.Text);
            if(!VmList.Items.OfType<VmRow>().Single(r=>r.Id==vm.Id).CanDisconnect)throw new Exception("Row disconnect unavailable for an open session.");
            if(!host.NativeClipboardSharing) throw new Exception("Automatic SPICE clipboard was not enabled");
            var originalHost=host;var secondVm=data.Vms.Single(v=>v.VmId==102);int beforeSwitch=requests.Count;
            SetTarget(secondVm);
            if(DisconnectButton.IsEnabled || VmPropertiesButton.IsEnabled || ManagementToggle.IsEnabled)throw new Exception("Background console enabled toolbar disconnect.");
            if(originalHost.NativeClipboardSharing || originalHost.Visibility!=Visibility.Hidden || !originalHost.SessionOpen)
                throw new Exception("Background SPICE session was closed or kept clipboard ownership.");
            EnsureHost();var secondHost=host;RememberConsole(secondVm);
            SetTarget(vm);await ConnectSelected();
            var existingRow=VmList.Items.OfType<VmRow>().Single(r=>r.Id==vm.Id);
            if(!existingRow.CanConnect)throw new Exception("Open console cannot be accessed from row.");
            VmList.SelectedItem=VmList.Items.OfType<VmRow>().Single(r=>r.Id==secondVm.Id);
            RowConnectClicked(new Button {DataContext=existingRow},new RoutedEventArgs(Button.ClickEvent));
            if(host!=originalHost || requests.Count!=beforeSwitch || consoles.Count!=2 || ConsoleArea.Child!=consoleDeck)
                throw new Exception("Returning to an open console recreated the session or requested a ticket.");
            if(!DisconnectButton.IsEnabled || !VmPropertiesButton.IsEnabled || !ManagementToggle.IsEnabled)throw new Exception("Restoring console did not enable toolbar disconnect.");
            var backgroundRow=VmList.Items.OfType<VmRow>().Single(r=>r.Id==secondVm.Id);
            if(!backgroundRow.CanDisconnect)throw new Exception("Background row disconnect unavailable.");
            RowDisconnectClicked(new Button {DataContext=backgroundRow},new RoutedEventArgs(Button.ClickEvent));
            if(host!=originalHost || !originalHost.SessionOpen || consoles.Count!=1 || !originalHost.NativeClipboardSharing)
                throw new Exception("Closing a background console affected the active session.");
            SetTarget(secondVm);EnsureHost();RememberConsole(secondVm);
            CloseConsoleFromUser(secondVm.Id);
            if(targetVm?.Id!=vm.Id || host!=originalHost || !HasDisplayedConsole || requests.Count!=beforeSwitch)
                throw new Exception("Closing active console did not restore the previous open session without reconnecting.");
            ShowSidebar(true);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"sessions-result.txt"),"PASS: SPICE session retained while switching; identical viewer restored without API requests; background clipboard disabled; closing background session preserves active connection; closing active session restores previous open console without API requests. Synthetic session only.");
            targetVm=targetVm! with {ClipboardEnabled=false}; ApplyConsolePreferences();
            if(host.NativeClipboardSharing) throw new Exception("Clipboard switch did not disable native sharing");
            targetVm=targetVm! with {ClipboardEnabled=true}; ApplyConsolePreferences();
            if(!host.NativeClipboardSharing) throw new Exception("Clipboard switch did not re-enable native sharing");
            Width = 1180; Height = 760; UpdateLayout();

            releaseProxy.TrySetResult(true);
            // The decoder is asynchronous; wait for the result, not a fixed UI delay.
            for(int wait=0;wait<100 && (host.SessionOpen || DisconnectButton.IsEnabled || CtrlAltDeleteButton.IsEnabled);wait++)await Task.Delay(100);
            if (!host.ConnectionStatus.Contains("channel event")) throw new Exception("Refused native connection not handled: " + host.ConnectionStatus);
            if (CtrlAltDeleteButton.IsEnabled || host.CanSendKeys) throw new Exception("Guest key action enabled without inputs channel");
            if(!VmList.Items.OfType<VmRow>().Single(r=>r.Id==vm.Id).CanConnect)throw new Exception("Row connect not restored after transport failure.");
            if(DisconnectButton.IsEnabled || VmPropertiesButton.IsEnabled || ManagementToggle.IsEnabled)throw new Exception("Toolbar disconnect enabled after transport failure.");
            bool rejectedKeys = false;
            try { host.SendCtrlAltDelete(); } catch (InvalidOperationException) { rejectedKeys = true; }
            if (!rejectedKeys) throw new Exception("Guest key action accepted without connection");
            // Both row double-click and the explicit button obtain fresh tickets.
            Activate();ShowSidebar(true);await Task.Delay(150);
            VmList.UpdateLayout(); var row = (DataGridRow)VmList.ItemContainerGenerator.ContainerFromItem(VmList.SelectedItem);
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = row };
            int beforeDoubleClick=requests.Count;
            VmList.RaiseEvent(click);
            for(int wait=0;wait<100 && (busy || host?.SessionOpen==true);wait++)await Task.Delay(100);
            if(requests.Count==beforeDoubleClick || requests.Count(p=>p.EndsWith("/migrated-node/qemu/101/spiceproxy"))!=2)
                throw new Exception("Row double-click did not request exactly one fresh ticket.");
            var blankClick=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left) {RoutedEvent=Control.MouseDoubleClickEvent,Source=VmList};
            beforeDoubleClick=requests.Count;
            VmList.RaiseEvent(blankClick);
            if(requests.Count!=beforeDoubleClick)throw new Exception("Double-click outside a VM row opened a console.");
            var connectRow=VmList.Items.OfType<VmRow>().Single(r=>r.Id==vm.Id);
            if(!connectRow.CanConnect)throw new Exception("Row reconnect unavailable after failure.");
            VmList.SelectedItem=VmList.Items.OfType<VmRow>().Single(r=>r.Id==secondVm.Id);
            RowConnectClicked(new Button {DataContext=connectRow},new RoutedEventArgs(Button.ClickEvent));
            for(int wait=0;wait<100 && (busy || host?.SessionOpen==true);wait++)await Task.Delay(100);
            if (requests.Count(p => p.EndsWith("/migrated-node/qemu/101/spiceproxy")) != 3) throw new Exception("Connect button / fresh ticket / migrated node check failed; tickets="+requests.Count(p=>p.EndsWith("/migrated-node/qemu/101/spiceproxy"))+"; busy="+busy+"; open="+host?.SessionOpen);
            string simulatedState = "stopped"; bool unavailable = false; int startRequests = 0, automaticTickets = 0;
            apiFactory = (s, key) =>
            {
                var api = new ProxmoxApi(s.Address, new SmokeHttp(req =>
                {
                    if (unavailable) throw new HttpRequestException("Synthetic outage");
                    var path = req.RequestUri!.AbsolutePath;
                    if (path.EndsWith("/config")) return "{\"data\":{\"vga\":\"qxl\"}}";
                    if (path.EndsWith("/spiceproxy")) { automaticTickets++; return config; }
                    if (path.EndsWith("/status/start")) { startRequests++; simulatedState = "running"; return "{\"data\":\"UPID:migrated-node:123:456:789:qmstart:101:test@pve!console:\"}"; }
                    if (path.Contains("/tasks/")) return "{\"data\":{\"status\":\"stopped\",\"exitstatus\":\"OK\"}}";
                    if (path.EndsWith("/status/current")) return $"{{\"data\":{{\"status\":\"{simulatedState}\",\"qmpstatus\":\"{simulatedState}\",\"uptime\":42}}}}";
                    return $"{{\"data\":[{{\"type\":\"qemu\",\"vmid\":101,\"node\":\"migrated-node\",\"name\":\"Guest\",\"status\":\"{simulatedState}\"}}]}}";
                })); api.UseToken(s.TokenId, key); return api;
            };
            await PollStatus();
            if (RuntimeLabel.Text != "■" || !RuntimeLabel.ToolTip.ToString()!.Contains("Stopped") || !StartButton.IsEnabled || StopButton.IsEnabled) throw new Exception("Stopped-state UI is incorrect");
            StartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            if (RuntimeLabel.Text != "▶" || startRequests != 1 || automaticTickets != 1 || !RuntimeLabel.ToolTip.ToString()!.Contains("Running") || StartButton.IsEnabled || !ShutdownButton.IsEnabled) throw new Exception("Start button / task result / running-state UI failed");
            // A guest-side shutdown must update controls even while inventory polling is occupied.
            simulatedState = "stopped"; polling = true;
            try { await PollStatus(); } finally { polling = false; }
            if (RuntimeLabel.Text != "■" || VmList.Items.OfType<VmRow>().Single(r=>r.Id==vm.Id).StatusSymbol != "■" || !StartButton.IsEnabled || ShutdownButton.IsEnabled || RebootButton.IsEnabled || StopButton.IsEnabled)
                throw new Exception("Power controls did not follow guest shutdown during inventory refresh");
            simulatedState = "running"; await PollStatus();
            if (StartButton.IsEnabled || !ShutdownButton.IsEnabled || !RebootButton.IsEnabled || !StopButton.IsEnabled)
                throw new Exception("Power controls did not follow external start");
            runtime[vm.Id] = new(new("running", "shutdown", 42), DateTimeOffset.UtcNow, null);
            Filter(); UpdateRuntimeHeader();
            if (RuntimeLabel.Text != "■" || VmList.Items.OfType<VmRow>().Single(r=>r.Id==vm.Id).StatusSymbol != "■"
                || ShutdownButton.IsEnabled || RebootButton.IsEnabled || StartButton.IsEnabled || !StopButton.IsEnabled)
                throw new Exception("Guest shutdown with a remaining QEMU process is misreported as running");
            runtime[vm.Id] = new(new("stopped", "paused", 0), DateTimeOffset.UtcNow, null);
            UpdateRuntimeHeader();
            if (RuntimeLabel.Text != "■") throw new Exception("Stopped VM did not take precedence over old QMP state");
            await PollStatus();
            using (var failedJobApi = new ProxmoxApi(server.Address, new SmokeHttp(_ => "{\"data\":{\"status\":\"stopped\",\"exitstatus\":\"ERROR: synthetic\"}}")))
            { try { await WaitForJob(failedJobApi, new("migrated-node", "UPID:migrated-node:test:"), CancellationToken.None); throw new Exception("Failed task reported success"); } catch (InvalidOperationException) { } }
            unavailable = true; await PollStatus();
            if (RuntimeLabel.Text != "⚠" || !RuntimeLabel.ToolTip.ToString()!.Contains("Unreachable") || StartButton.IsEnabled || StopButton.IsEnabled) throw new Exception("Unavailable status misreported as stopped or actions enabled");
            unavailable = false; await PollStatus();
            Search.Text = "Linux"; if (VmList.Items.Count != 1) throw new Exception("Filter failed"); Search.Clear();
            ApplyInventory(server,new[] { new Vm(101,"node","Template","stopped",true) }); Filter();
            if(VmList.Items.Cast<VmRow>().Any(v=>v.VmId==101)||targetVm!=null)throw new Exception("Saved template was not hidden/deselected.");
            ApplyInventory(server,new[] {new Vm(101,"node","Guest","running")}); Filter(vm.Id);
            if(VmList.Items.Count!=2)throw new Exception("VM converted back from template did not reappear.");
            consoleInfo[vm.Id]=new(new VmConsoleInfo(false,"std"),DateTimeOffset.UtcNow); Filter(vm.Id);
            if(VmList.Items.Cast<VmRow>().Single(v=>v.VmId==101).ConsoleType!="noVNC")throw new Exception("Automatic non-QXL console label incorrect.");
            SetTarget(vm);
            if(guestKeysMenu.Items[0] is not MenuItem firstShortcut || (string)firstShortcut.Header!="Ctrl+Alt+Delete")throw new Exception("Ctrl+Alt+Delete must be first.");
            await CheckUnifiedMenus();
            SpiceHost.CheckInputMapping();
            await host.CheckWpfRendering(i => { Width = 1180 + (i % 2) * 40; Height = 760 + (i % 3) * 20; UpdateLayout(); });
            CloseConsole(vm.Id); host = null; ConsoleArea.Child = new TextBlock { Text = "SPICE console area\n\nWPF embedding tested locally.\nNo real VM connected.", FontSize = 22, Foreground = Brushes.SlateGray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            await CheckConsoleLayout();
            if(Environment.GetCommandLineArgs().Contains("--novnc")) {
                using var terminalTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var terminal=new NoVncHost("serial0");ConsoleArea.Child=terminal;UpdateLayout();
                await terminal.InitializeAsync(terminalTimeout.Token);
                await terminal.CheckSerialSessionAsync(terminalTimeout.Token);
                ConsoleArea.Child=null;
            }
            if(Environment.GetCommandLineArgs().Contains("--novnc"))
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var viewer=new NoVncHost(); ConsoleArea.Child=viewer; UpdateLayout();
                await viewer.InitializeAsync(timeout.Token);
                vncHost=viewer;RememberConsole(vm);
                await viewer.CheckSyntheticSessionAsync(timeout.Token, async () => {
                    int requestCount=requests.Count;
                    SetTarget(secondVm);EnsureHost();RememberConsole(secondVm);
                    SetTarget(vm);await ConnectSelected();
                    var frame=await viewer.GetFrameStatsAsync(timeout.Token);
                    if(vncHost!=viewer || requests.Count!=requestCount || !viewer.SessionOpen || frame.Nonblack!=3072)
                        throw new Exception("noVNC session/framebuffer lost or reconnected after switching.");
                    CloseConsole(secondVm.Id);
                    if(!viewer.SessionOpen)throw new Exception("Closing background SPICE affected noVNC.");
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"sessions-result.txt"),"\nPASS: synthetic noVNC session and framebuffer survive switching to SPICE and back without API requests; closing background SPICE preserves noVNC.");
                });
                await CheckConsoleLayout();
            if(Environment.GetCommandLineArgs().Contains("--novnc")) {
                using var terminalTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var terminal=new NoVncHost("serial0");ConsoleArea.Child=terminal;UpdateLayout();
                await terminal.InitializeAsync(terminalTimeout.Token);
                await terminal.CheckSerialSessionAsync(terminalTimeout.Token);
                ConsoleArea.Child=null;
            }
                CloseConsoleFromUser(vm.Id);
                if(targetVm!=null || VmList.SelectedItem!=null || VmToolbar.Visibility!=Visibility.Collapsed || ShutdownButton.IsEnabled || RebootButton.IsEnabled || StopButton.IsEnabled || ConsoleArea.Child is not StackPanel)
                    throw new Exception("Closing last console did not clear selection and show neutral home.");
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"sessions-result.txt"),"\nPASS: closing last console clears VM target and selection, hides VM toolbar and shows home navigation.");
            }
            ConsoleTitle.Text = "Windows Test-VM"; Status("Local test completed · No connection to a real Proxmox VM tested.");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "wpf-preview.png"))) encoder.Save(file);
            await CheckVmFolders(server);
            await CheckConsoleQueue(server);
            await CheckFastStatus();
            await CheckInventorySync(server);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-result.txt"), "PASS: WPF startup, profile reload, DPAPI, shared SPICE worker and native GDI surface, local native error handling, row double-click and Connect button routing, fresh tickets, current-node lookup, filtering, stopped/running/unreachable state UI, start action and task failure handling, disconnected Ctrl+Alt+Delete guard, status symbols/tooltips, native clipboard enable/disable/re-enable, SPICE input mapping, six native 32-bit frames verified with GDI capture across resizing, rendering and disposal. No live Proxmox VM tested.");
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-error.txt"), e.ToString()); Environment.ExitCode = 1; }
        finally { Close(); }
    }
    sealed class SmokeHttp(Func<HttpRequestMessage, string> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(request)) }); }
}



































