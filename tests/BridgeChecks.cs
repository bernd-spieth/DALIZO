// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using ProxmoxSpice;
static class BridgeChecks
{
    public static async Task Run()
    {
        int calls=0;
        using var api=new ProxmoxApi("https://pve.example.test:8006",new Fake(req=>{
            calls++;
            if(req.Method!=HttpMethod.Get||req.RequestUri!.PathAndQuery!="/api2/json/nodes/node/network?type=any_bridge")throw new Exception("Wrong bridge query.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""{"data":[{"iface":"vmbr1","type":"OVSBridge"},{"iface":"vmbr0","type":"bridge"},{"iface":"vmbr0","type":"bridge"},{"iface":"zone1","type":"vnet"},{"iface":"eth0","type":"eth"}]}""")});
        }));
        api.UseToken("test@pve!client","synthetic-secret");
        if(!(await api.GetBridgesAsync("node")).SequenceEqual(new[]{"vmbr0","vmbr1","zone1"})||calls!=1)throw new Exception("Bridge filtering/order failed.");
        using var denied=new ProxmoxApi("https://pve.example.test:8006",new Fake(req=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("{\"data\":null}")})));
        denied.UseToken("test@pve!client","synthetic-secret");
        bool failed=false;try{await denied.GetBridgesAsync("node");}catch(HttpRequestException){failed=true;}
        if(!failed)throw new Exception("Permission failure was hidden.");
        Console.WriteLine("PASS: bridge query, Linux/OVS/SDN choices, deduplication, sorting and permission failure. Synthetic HTTP only.");
    }
}
