// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Net;
using System.Net.Http;
namespace ProxmoxSpice;
public partial class MainWindow
{
    sealed class DeferredStatusHttp(TaskCompletionSource reached, Task release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
        {
            string body;
            if(req.RequestUri!.AbsolutePath.EndsWith("/config")) {
                reached.TrySetResult();await release.WaitAsync(ct);body="{\"data\":{\"vga\":\"std\"}}";
            } else body="{\"data\":[{\"type\":\"qemu\",\"vmid\":101,\"node\":\"test\",\"name\":\"Guest\",\"status\":\"running\"},{\"type\":\"qemu\",\"vmid\":102,\"node\":\"test\",\"name\":\"Linux\",\"status\":\"stopped\"}]}";
            return new(HttpStatusCode.OK){Content=new StringContent(body)};
        }
    }
    async Task CheckFastStatus()
    {
        var factory=apiFactory;var previous=targetVm;
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? pending=null;
        try {
            SetTarget(null);runtime.Clear();consoleInfo.Clear();Filter();
            apiFactory=(server,secret)=>{var api=new ProxmoxApi(server.Address,new DeferredStatusHttp(reached,release.Task));api.UseToken(server.TokenId,secret);return api;};
            pending=PollStatus();await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if(pending.IsCompleted)throw new Exception("Deferred configuration request did not hold the polling pass.");
            if(VmList.Items.OfType<VmRow>().Single(r=>r.VmId==101).RuntimeStatus!="Running" || VmList.Items.OfType<VmRow>().Single(r=>r.VmId==102).RuntimeStatus!="Stopped")
                throw new Exception("VM status was not displayed while configuration requests were pending.");
            release.TrySetResult();await pending;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"fast-status-result.txt"),"PASS: running/stopped rows displayed from bulk inventory while configuration replies deliberately blocked; polling completes after release. Synthetic HTTP; no live-server latency claim.");
        } finally {release.TrySetResult();if(pending!=null)await pending;apiFactory=factory;SetTarget(previous);}
    }
}
