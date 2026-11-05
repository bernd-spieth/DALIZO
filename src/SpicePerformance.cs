// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace ProxmoxSpice;
public partial class MainWindow
{
    async Task CompareSpicePerformance(ProxmoxApi api, Vm vm, ServerProfile server, CancellationToken ct)
    {
        var report = new StringBuilder("Passive same-VM comparison. No guest workload, clicks, power or configuration changes. Diagnostic pointer movement and Shift wake-up are noted below. CPU is processor-seconds / wall-seconds (one logical core = 100%). Frame counts are native GDI presentations, not distinct guest frames.\n");
        var path = Path.Combine(AppContext.BaseDirectory,"spice-performance.txt");
        async Task<string> Ticket() => await api.GetConsoleAsync(vm,string.IsNullOrWhiteSpace(server.Proxy)?api.Host:server.Proxy,ct);
        try {
            foreach(bool legacy in Environment.GetCommandLineArgs().Contains("--native-only") ? Array.Empty<bool>() : new[]{false}) {
                host = new SpiceHost(receiveOnly:true,legacyPresentation:legacy);
                host.SetResizeEnabled(false); host.SetClipboardSharing(false);
                ConsoleArea.Child=host; UpdateLayout(); host.Connect(SpiceConfig.Parse(await Ticket()));
                await Task.Delay(4000,ct);
                if(legacy) { host.WakeKeyboardForDiagnostic(); await Task.Delay(1500,ct); report.AppendLine("Sent a diagnostic Shift press/release to wake the display; no text or clicks."); }
                using var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;
                var watch=Stopwatch.StartNew();var frames=host.FramesPresented;var requests=host.SnapshotRequests;
                await Task.Delay(10000,ct);
                var utilization=(process.TotalProcessorTime-cpu).TotalSeconds/watch.Elapsed.TotalSeconds*100;
                string pixels;
                try { pixels=host.VerifyReceivedFrame(); } catch(InvalidOperationException) { pixels="No non-black content verified"; }
                report.AppendLine($"DALIZO {(legacy?"previous":"native worker")}: CPU={utilization:F2}% of one core; presentations={host.FramesPresented-frames}; snapshots={host.SnapshotRequests-requests}; size={host.FrameSize}; {pixels}");
                if(!legacy) {
                    host.Visibility=Visibility.Hidden; frames=host.FramesPresented;
                    await Task.Delay(1000,ct);
                    if(host.FramesPresented!=frames)throw new Exception("Hidden console continued presenting images");
                    host.Visibility=Visibility.Visible;await Task.Delay(400,ct);
                    if(host.FramesPresented==frames)throw new Exception("Console did not resume presentation");
                    report.AppendLine("PASS: hidden presentation pauses; visible presentation resumes.");
                }
                host.Dispose();host=null;ConsoleArea.Child=null;
            }
            var executable=Directory.GetDirectories(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"VirtViewer*")
                .Select(p=>Path.Combine(p,"bin","remote-viewer.exe")).First(File.Exists);
            var ticketPath=Path.Combine(AppContext.BaseDirectory,"performance-"+Guid.NewGuid()+".vv");
            Process? native=null;
            try {
                var ticket=await Ticket();
                var lines=ticket.Split('\n').Where(l=>!l.StartsWith("delete-this-file=") && !l.StartsWith("disable-channels="));
                await File.WriteAllTextAsync(ticketPath,string.Join('\n',lines)+"\ndelete-this-file=1\ndisable-channels=inputs;playback;record;usbredir\n",ct);
                var start=new ProcessStartInfo(executable) {UseShellExecute=false,WindowStyle=ProcessWindowStyle.Normal};
                start.ArgumentList.Add("--auto-resize=never");start.ArgumentList.Add("--spice-disable-audio");start.ArgumentList.Add("--spice-disable-usbredir");start.ArgumentList.Add(ticketPath);
                native=Process.Start(start) ?? throw new Exception("Native viewer did not start");
                await Task.Delay(4000,ct);
                if(native.HasExited)throw new Exception("Native viewer exited before measurement");
                var cpu=native.TotalProcessorTime;var watch=Stopwatch.StartNew();
                await Task.Delay(10000,ct);
                if(native.HasExited)throw new Exception("Native viewer exited during measurement");
                native.Refresh();
                report.AppendLine($"Native remote-viewer: CPU={(native.TotalProcessorTime-cpu).TotalSeconds/watch.Elapsed.TotalSeconds*100:F2}% of one core; native frame rate not instrumented. Process remained running; connection/display requires separate visual verification.");
            } finally {
                if(native!=null) { if(!native.HasExited) {native.CloseMainWindow();await Task.Delay(500);if(!native.HasExited)native.Kill();}native.Dispose(); }
                if(File.Exists(ticketPath))File.Delete(ticketPath);
            }
            report.AppendLine("Limits: passive sequential samples, no standardized animation, no end-to-end input latency measurement. Results are not an RDP comparison or a guarantee of equivalent performance.");
        } finally {host?.Dispose();host=null;await File.WriteAllTextAsync(path,report.ToString());}
    }
}
