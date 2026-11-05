// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Threading;
class EngineTests {
    [STAThread] static int Main(){
        var runtime=Directory.GetDirectories(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"VirtViewer*").Select(p=>Path.Combine(p,"bin")).First(p=>File.Exists(Path.Combine(p,"libspice-client-glib-2.0-8.dll")));
        N.SetDllDirectory(runtime);
        WorkerTests.Run();PresenterTests.Run();
        var events=new System.Collections.Generic.List<string>();
        SpiceWorker.SendChordCore(new uint[]{0x15b,0x12},(key,down)=>events.Add(key.ToString("X")+":"+down));
        if(string.Join(",",events)!="15B:True,12:True,12:False,15B:False")throw new Exception("Extended modifier chord order.");
        events.Clear();
        try{SpiceWorker.SendChordCore(new uint[]{0x1d,0x38,0x153},(key,down)=>{
            if(key==0x153&&down)throw new InvalidOperationException("Synthetic transport error");
            events.Add(key.ToString("X")+":"+down);
        });}catch(InvalidOperationException){}
        if(string.Join(",",events)!="1D:True,38:True,38:False,1D:False")throw new Exception("Modifiers not released after failed chord.");
        Console.WriteLine("PASS: extended modifier order and reverse release after partial chord failure.");
        using(var first=new SpiceWorker())using(var second=new SpiceWorker()){
            if(first.ThreadId!=second.ThreadId)throw new Exception("Sessions use different GLib owner threads.");
            first.Probe();second.Probe();Thread.Sleep(100);first.Dispose();
            second.ResetMetrics();second.Probe();Thread.Sleep(100);
            if((int)second.Metrics()["worker_dispatch_samples"]!=1)throw new Exception("Closing one console stopped another.");
        }
        if(NativeConsole.DecodeScan(0x001e0001)!=0x1e||NativeConsole.DecodeScan(0x011d0001)!=0x11d||NativeConsole.DecodeScan(0x01530001)!=0x153)throw new Exception("Physical/extended scan-code regression.");
        using(var worker=new SpiceWorker()){
            worker.SetClipboardSharing(false);worker.OfferClipboard("test");
            if(worker.TryClipboard(out _))throw new Exception("Disconnected clipboard returned data.");
            bool limited=false;try{worker.OfferClipboard(new string('x',4*1024*1024+1));}catch(InvalidOperationException){limited=true;}
            if(!limited)throw new Exception("Clipboard size bound missing.");
        }
        Console.WriteLine("PASS: shared GLib owner, independent session disposal, extended scan codes, disconnected clipboard and clipboard size limit.");
        return 0;
    }
}