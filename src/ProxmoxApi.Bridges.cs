// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ProxmoxSpice;
public sealed partial class ProxmoxApi
{
    public async Task<IReadOnlyList<string>> GetBridgesAsync(string node,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(node))throw new ArgumentException("A VM node is required to list bridges.");
        var data=await ReadDataAsync($"api2/json/nodes/{Uri.EscapeDataString(node)}/network?type=any_bridge",ct);
        if(data.ValueKind!=JsonValueKind.Array)throw new InvalidOperationException("The server returned an invalid bridge list.");
        return data.EnumerateArray().Where(v=>v.TryGetProperty("iface",out var name)&&name.ValueKind==JsonValueKind.String)
            .Where(v=>!v.TryGetProperty("type",out var type)||type.GetString() is "bridge" or "OVSBridge" or "vnet")
            .Select(v=>v.GetProperty("iface").GetString()!).Where(n=>Regex.IsMatch(n,@"^[a-zA-Z0-9][a-zA-Z0-9_.:-]{0,63}$"))
            .Distinct(StringComparer.Ordinal).OrderBy(n=>n,StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
