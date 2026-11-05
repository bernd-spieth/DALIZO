// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Text.Json;
namespace ProxmoxSpice;
public sealed partial class ProxmoxApi
{
    public async Task<VncTicket> GetSerialTicketAsync(Vm vm,string serial,CancellationToken ct=default)
    {
        if(serial is not ("serial0" or "serial1" or "serial2" or "serial3"))throw new ArgumentException("Select serial0, serial1, serial2 or serial3.");
        using var body=new FormUrlEncodedContent(new Dictionary<string,string>{{"serial",serial}});
        using var response=await http.PostAsync(VncPath(vm)+"termproxy",body,ct);await CheckAsync(response);
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));var data=doc.RootElement.GetProperty("data");
        var user=data.GetProperty("user").GetString();var ticket=data.GetProperty("ticket").GetString();
        int port=int.Parse(data.GetProperty("port").ToString());
        if(string.IsNullOrWhiteSpace(user)||user.Length>512||user.Any(char.IsControl)||user.Contains(':')||string.IsNullOrEmpty(ticket)||ticket.Length>4096||ticket.Any(char.IsControl)||port is <5900 or >5999)
            throw new InvalidOperationException("Invalid serial console connection details.");
        return new(user,ticket,ticket,port);
    }
}
