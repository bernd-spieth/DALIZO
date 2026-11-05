// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ProxmoxSpice;

public sealed record Vm(int Id, string Node, string Name, string Status, bool IsTemplate = false);

public sealed partial class ProxmoxApi : IDisposable
{
    private readonly HttpClient http;
    public string Host { get; }

    public ProxmoxApi(string address, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ArgumentException("Enter an HTTPS address without a path, for example https://pve.example.org:8006");
        Host = uri.Host;
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(25) };
    }

    public void UseToken(string id, string secret)
    {
        if (!id.Contains('@') || !id.Contains('!') || string.IsNullOrWhiteSpace(secret) ||
            id.Any(char.IsControl) || secret.Any(char.IsControl))
            throw new ArgumentException("Enter a token ID in user@realm!token format and the token secret.");
        http.DefaultRequestHeaders.Remove("Authorization");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"PVEAPIToken={id}={secret}");
    }

    public async Task<IReadOnlyList<Vm>> ListVmsAsync(CancellationToken ct = default, bool includeTemplates = false)
    {
        using var response = await http.GetAsync("api2/json/cluster/resources?type=vm", ct);
        await CheckAsync(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Where(v => v.GetProperty("type").GetString() == "qemu")
            .Select(v => new Vm(v.GetProperty("vmid").GetInt32(), v.GetProperty("node").GetString()!,
                v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                v.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown",
                v.TryGetProperty("template", out var t) && (t.ValueKind == JsonValueKind.True || t.ToString() == "1")))
            .Where(v => includeTemplates || !v.IsTemplate)
            .OrderBy(v => v.Id).ToArray();
    }

    public async Task<string> GetConsoleAsync(Vm vm, string proxy, CancellationToken ct = default)
    {
        if (vm.Id <= 0 || string.IsNullOrWhiteSpace(vm.Node)) throw new ArgumentException("Invalid VM selection.");
        if (Uri.CheckHostName(proxy) == UriHostNameType.Unknown) throw new ArgumentException("Enter a SPICE proxy hostname or IP address without a port.");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"api2/spiceconfig/nodes/{Uri.EscapeDataString(vm.Node)}/qemu/{vm.Id}/spiceproxy")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["proxy"] = proxy }) };
        using var response = await http.SendAsync(request, ct);
        await CheckAsync(response);
        var config = await response.Content.ReadAsStringAsync(ct);
        if (!config.TrimStart().StartsWith("[virt-viewer]", StringComparison.Ordinal) || config.Length > 131072)
            throw new InvalidOperationException("The server did not return a valid SPICE configuration.");
        return config;
    }

    private static Task CheckAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Authentication rejected or session expired. Sign in again.",
                HttpStatusCode.Forbidden => "Access denied. Check VM list and console permissions.",
                _ => $"Proxmox request failed (HTTP {(int)response.StatusCode})."
            });
        return Task.CompletedTask;
    }

    public void Dispose() => http.Dispose();
}





