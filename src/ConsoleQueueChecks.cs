// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Threading;
namespace ProxmoxSpice;
public partial class MainWindow
{
    sealed class QueueConsoleHttp : HttpMessageHandler
    {
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource OthersDone=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests,Active,Peak,Completed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
        {
            Requests++;Active++;Peak=Math.Max(Peak,Active);
            try {
                if(req.RequestUri!.AbsolutePath.Contains("/10001/"))await Release.Task.WaitAsync(ct);
                else {await Task.Delay(15,ct);if(++Completed==31)OthersDone.TrySetResult();}
                return new(HttpStatusCode.OK){Content=new StringContent("{\"data\":{\"vga\":\"qxl\"}}")};
            } finally {Active--;}
        }
    }
    async Task CheckConsoleQueue(ServerProfile server)
    {
        var original=data;var previous=targetVm;var query=Search.Text;
        using var handler=new QueueConsoleHttp();using var api=new ProxmoxApi(server.Address,handler);
        Task? pending=null;
        try {
            SetTarget(null);Search.Text="";
            data=new(1,original.Servers.ToList(),Enumerable.Range(1,32).Select(i=>new SavedVm(Guid.NewGuid(),server.Id,10000+i,"Queue "+i)).ToList());Filter();
            var inventory=data.Vms.Select(v=>new Vm(v.VmId,"node",v.Name,"running")).ToArray();
            pending=RefreshConsoleInfo(api,server,inventory);
            await handler.OthersDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            if(pending.IsCompleted || handler.Requests!=32 || handler.Peak>4 || VmList.Items.OfType<VmRow>().Count(r=>r.ConsoleType=="SPICE")!=31)
                throw new Exception("Console queue did not publish all fast results while one request was pending.");
            handler.Release.TrySetResult();await pending;
            await RefreshConsoleInfo(api,server,inventory);
            if(handler.Requests!=32 || VmList.Items.OfType<VmRow>().Any(r=>r.ConsoleType!="SPICE"))throw new Exception("Console cache or final result failed.");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"console-queue-result.txt"),"PASS: 32 VMs checked in one pass; maximum four concurrent configuration requests; 31 console labels published while one response blocked; all labels resolved after release; immediate repeat uses cache without new requests. Synthetic HTTP.");
        } finally {handler.Release.TrySetResult();if(pending!=null)await pending;data=original;Search.Text=query;Filter();SetTarget(previous);}
    }
}
