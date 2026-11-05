// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
namespace ProxmoxSpice;

// Never use a record here: its generated ToString would expose the short-lived ticket.
public sealed class VncTicket(string user, string password, string ticket, int port)
{
    public string User { get; } = user;
    public string Password { get; } = password;
    public string Ticket { get; } = ticket;
    public int Port { get; } = port;
}
public sealed partial class ProxmoxApi
{
    static string VncPath(Vm vm)
    {
        if(vm.Id <= 0 || string.IsNullOrWhiteSpace(vm.Node)) throw new ArgumentException("Invalid VM selection.");
        return $"api2/json/nodes/{Uri.EscapeDataString(vm.Node)}/qemu/{vm.Id}/";
    }
    public async Task<VncTicket> GetVncTicketAsync(Vm vm, CancellationToken ct = default)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string,string> { ["websocket"]="1" });
        using var response = await http.PostAsync(VncPath(vm)+"vncproxy",body,ct);
        await CheckAsync(response);
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var data=doc.RootElement.GetProperty("data");
        var user=data.GetProperty("user").GetString();
        var ticket=data.GetProperty("ticket").GetString();
        var portValue=data.GetProperty("port");
        int port=portValue.ValueKind==JsonValueKind.Number ? portValue.GetInt32() : int.Parse(portValue.GetString()!);
        var password=data.TryGetProperty("password",out var p) ? p.GetString() : ticket;
        if(string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(ticket) || ticket.Length>4096 ||
           string.IsNullOrEmpty(password) || password.Length>4096 || port is <5900 or >5999)
            throw new InvalidOperationException("Invalid noVNC connection details returned by the server.");
        return new(user,password,ticket,port);
    }
    public Uri VncWebSocketUri(Vm vm,VncTicket ticket)
    {
        if(ticket.Port is <5900 or >5999) throw new ArgumentException("Invalid VNC port.");
        return new UriBuilder(new Uri(http.BaseAddress!,VncPath(vm)+"vncwebsocket")) {
            Scheme="wss", Query=$"port={ticket.Port}&vncticket={Uri.EscapeDataString(ticket.Ticket)}"
        }.Uri;
    }
    public async Task<ClientWebSocket> OpenVncSocketAsync(Vm vm,VncTicket ticket,CancellationToken ct)
    {
        var socket=new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization",http.DefaultRequestHeaders.GetValues("Authorization").Single());
        socket.Options.AddSubProtocol("binary");
        socket.Options.KeepAliveInterval=TimeSpan.FromSeconds(20);
        try { await socket.ConnectAsync(VncWebSocketUri(vm,ticket),ct); return socket; }
        catch(OperationCanceledException) { socket.Dispose(); throw; }
        catch(Exception error) { socket.Dispose(); throw new InvalidOperationException("noVNC WebSocket rejected. Check the HTTPS certificate, VM.Console permissions, WebSocket proxy and API-token support in your Proxmox version.",error); }
    }
}


