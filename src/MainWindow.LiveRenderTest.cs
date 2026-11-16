// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows;
namespace ProxmoxSpice;
public partial class MainWindow
{
    // Explicit diagnostic mode: only list/query/connect; no power/configuration or input commands.
    async Task LiveRenderTest(string vmName)
    {
        var resultPath = Path.Combine(AppContext.BaseDirectory,"live-render-result.txt");
        try
        {
            IsEnabled = false;
            var matches = data.Vms.Where(v => string.Equals(v.Name,vmName,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matches.Length!=1) throw new InvalidOperationException("Exactly one saved VM with the requested name is required.");
            var vm=matches[0]; var server=data.Servers.Single(s=>s.Id==vm.ServerId);
            if(string.IsNullOrEmpty(server.ProtectedSecret)) throw new InvalidOperationException("No saved token; interactive credentials were not requested.");
            using var api = new ProxmoxApi(server.Address,ignoreCertificateErrors:server.IgnoreCertificateErrors);
            api.UseToken(server.TokenId, SecretProtection.Unprotect(server.ProtectedSecret));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            if(Environment.GetCommandLineArgs().Contains("--inventory-check"))
            {
                var inventory=await api.ListVmsAsync(timeout.Token,includeTemplates:true);
                ApplyInventory(server,inventory); Filter();
                if(VmList.Items.Cast<VmRow>().Any(row=>templates.Contains(row.Id)))throw new InvalidOperationException("Template visible after inventory refresh.");
                var check=inventory.Single(v=>v.Id==vm.VmId);
                var info=await api.GetConsoleInfoAsync(check,timeout.Token);
                bool usable=await api.CanUseVmAsync(check.Id,timeout.Token);
                consoleInfo[vm.Id]=new(info,DateTimeOffset.UtcNow); Filter();
                File.WriteAllText(resultPath,$"PASS: read-only inventory and effective token permissions; {inventory.Count(v=>v.IsTemplate)} server templates excluded; {templates.Count} saved templates hidden; {vmName} usable={usable}, current display={info.DisplayType}, Auto={info.Resolve("Auto")}; no console connection, input, profile save or VM write.");
                return;
            }
            var all=await api.ListVmsAsync(timeout.Token);
            var current=all.Single(v=>v.Id==vm.VmId);
            if(Environment.GetCommandLineArgs().Contains("--disk-access-check"))
            {
                var rights=await api.GetDevicePermissionsAsync(current.Id,timeout.Token);
                var stores=await api.StorageChoicesAsync(current.Node,"images",timeout.Token);
                File.WriteAllText(resultPath,$"VM {current.Id}: VM.Config.Disk={rights.Disk}; visible disk storage: {string.Join(", ",stores.DefaultIfEmpty("(none)"))}");
                return;
            }
            if(current.Status!="running") throw new InvalidOperationException("VM is not running; no start action was performed.");
            if(Environment.GetCommandLineArgs().Contains("--novnc"))
            {
                vncHost=new NoVncHost(); ConsoleArea.Child=vncHost; UpdateLayout();
                await vncHost.InitializeAsync(timeout.Token);
                await vncHost.ConnectAsync(api,current,timeout.Token,receiveOnly:true);
                NoVncHost.FrameStats? frame=null;
                for(int i=0;i<30;i++) {
                    await Task.Delay(300,timeout.Token);
                    frame=await vncHost.GetFrameStatsAsync(timeout.Token);
                    if(frame.Width>0 && frame.Height>0 && frame.Nonblack>0)break;
                }
                bool vncWoke=false;
                if(frame?.Nonblack==0 && Environment.GetCommandLineArgs().Contains("--wake-pointer")) {
                    vncHost.WakeForDiagnostic(); vncWoke=true; await Task.Delay(3000,timeout.Token);
                    frame=await vncHost.GetFrameStatsAsync(timeout.Token);
                }
                bool vncShift=Environment.GetCommandLineArgs().Contains("--exercise-input");
                if(vncShift) { vncHost.ShiftForDiagnostic(); await Task.Delay(3000,timeout.Token); frame=await vncHost.GetFrameStatsAsync(timeout.Token); }
                if(frame==null || frame.Nonblack==0)throw new InvalidOperationException($"noVNC connected but no non-black frame verified; {frame}; {vncHost.TransportStats}; pointerWake={vncWoke}.");
                File.WriteAllText(resultPath,$"PASS: noVNC API-token authentication, WebSocket and embedded browser image for {vmName}; {frame.Width}x{frame.Height}; nonblack={frame.Nonblack}; pointerWake={vncWoke}; shiftKeyTest={vncShift}; clipboard disabled; no clicks, text, power or configuration actions.");
                return;
            }
            if(Environment.GetCommandLineArgs().Contains("--performance-compare")) { await CompareSpicePerformance(api,current,server,timeout.Token); File.WriteAllText(resultPath,"PASS: performance comparison completed; see spice-performance.txt for limitations."); return; }
            var config=SpiceConfig.Parse(await api.GetConsoleAsync(current,string.IsNullOrWhiteSpace(server.Proxy)?api.Host:server.Proxy,timeout.Token));
            if(Environment.GetCommandLineArgs().Contains("--media-test")){await RunMediaDiagnostic(vm,config);return;}
            bool exerciseInput=Environment.GetCommandLineArgs().Contains("--exercise-input");
            bool resizeTest=Environment.GetCommandLineArgs().Contains("--resize-test");
            host=new SpiceHost(receiveOnly:!exerciseInput && !resizeTest); host.SetResizeEnabled(false); host.SetClipboardSharing(false); ConsoleArea.Child=host; UpdateLayout();
            string lastStatus=""; host.StatusChanged += text => lastStatus=text;
            host.Connect(config,vm.SpiceAudioEnabled,vm.PreferSpiceMjpeg);
            for(int i=0;i<150;i++) await Task.Delay(100,timeout.Token);
            if(Environment.GetCommandLineArgs().Contains("--usb-inventory")){
                var hardwareConfig=await api.GetConfigurationAsync(current,timeout.Token,current:true);
                var disks=hardwareConfig.Values.Where(p=>VmConfiguration.IsDrive(p.Key)).Select(p=>p.Key);
                var devices=await host.ListUsb().WaitAsync(timeout.Token);
                File.WriteAllText(resultPath,$"PASS: VM {vm.VmId} drive slots {string.Join(", ",disks)}; SPICE USB inventory returned {devices.Count} local devices. No redirection or VM configuration change.");
                return;
            }
            if(Environment.GetCommandLineArgs().Contains("--clipboard-capabilities"))
            {
                if(host.NativeClipboardSharing) throw new InvalidOperationException("Read-only diagnostic unexpectedly shares clipboard");
                if(!host.GuestAgentConnected) throw new InvalidOperationException("SPICE guest agent is not connected; guest clipboard requires SPICE Guest Tools/agent.");
                File.WriteAllText(resultPath,"PASS: SPICE guest agent connected for "+vmName+"; read-only clipboard sharing disabled; no local or guest clipboard contents read or changed.");
                return;
            }
            if(!host.HasFrame) throw new InvalidOperationException("No framebuffer: "+lastStatus);
            if(resizeTest) {
                var original=host.FrameSize;
                try {
                    if(!host.GuestAgentConnected)throw new InvalidOperationException("SPICE agent unavailable.");
                    SetConsoleFocus(true);host.Width=800;host.Height=600;UpdateLayout();host.SetResizeEnabled(true);
                    await WaitForGuestSize(800,600,timeout.Token);
                    host.SetResizeEnabled(false);int count=host.ResizeRequests;
                    host.Width=760;host.Height=520;UpdateLayout();await Task.Delay(1500,timeout.Token);
                    if(host.ResizeRequests!=count)throw new InvalidOperationException("Disabled auto resize still sent a request.");
                    host.SetResizeEnabled(true);await WaitForGuestSize(760,520,timeout.Token);
                    File.WriteAllText(resultPath,"PASS: live SPICE guest resize 800x600 then 760x520; disable/re-enable respected; no guest keys, clicks, clipboard or Proxmox configuration writes.");
                }
                finally {
                    host.RestoreResolutionForDiagnostic(original.Width,original.Height);
                    try { await WaitForGuestSize(original.Width,original.Height,timeout.Token); File.AppendAllText(resultPath,$" Original guest resolution {original.Width}x{original.Height} restored."); }
                    catch { File.WriteAllText(resultPath,$"FAIL: original guest resolution {original.Width}x{original.Height} restoration not confirmed; current {host.FrameSize}."); }
                }
                return;
            }
            if(exerciseInput) { host.ExerciseInputRoutingForDiagnostic(); await Task.Delay(3000,timeout.Token); }
            var hardware=await api.GetConfigurationAsync(current,timeout.Token,current:true);
            host.ActiveAudioDevice=hardware.Get("audio0");
            string mediaEvidence=host.MediaDetails+"; VM audio0: "+hardware.Values.GetValueOrDefault("audio0","not configured");
            string evidence; bool woke=false;
            try { evidence=host.VerifyReceivedFrame(); }
            catch(InvalidOperationException) when (Environment.GetCommandLineArgs().Contains("--wake-pointer") && host.HasFrame)
            {
                host.WakePointerOnceForDiagnostic(); woke=true;
                await Task.Delay(3000,timeout.Token);
                try { evidence=host.VerifyReceivedFrame(); }
                catch(InvalidOperationException ex){throw new InvalidOperationException(ex.Message+" "+host.SessionPhase+"; "+mediaEvidence,ex);}
            }
            File.WriteAllText(resultPath,"PASS: console image test for "+vmName+"; "+evidence+"; "+host.SessionPhase+"; "+mediaEvidence+"; pointerWake="+woke+"; inputExercise="+exerciseInput+"; no guest text, mouse clicks, power or configuration commands sent.");
        }
        catch(Exception error) { File.WriteAllText(resultPath,"FAIL: "+error.GetType().Name+": "+error.Message); }
        finally { host?.Dispose(); host=null; vncHost?.Dispose(); vncHost=null; Close(); }
    }
    async Task WaitForGuestSize(int width,int height,CancellationToken ct)
    {
        for(int i=0;i<120;i++) { if(host!.FrameSize==(width,height))return; await Task.Delay(100,ct); }
        throw new InvalidOperationException($"Guest resize not confirmed: expected {width}x{height}, received {host!.FrameSize}; requests={host.ResizeRequests}.");
    }
}




