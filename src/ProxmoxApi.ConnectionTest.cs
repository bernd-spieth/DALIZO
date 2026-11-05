// SPDX-License-Identifier: GPL-3.0-only
namespace ProxmoxSpice;

public sealed partial class ProxmoxApi
{
    public static readonly (string Privilege, string Feature)[] RequiredVmPermissions =
    {
        ("VM.Audit", "Status and configuration"),
        ("VM.Console", "Console"),
        ("VM.PowerMgmt", "Start, shutdown, reboot and stop"),
        ("VM.Config.CPU", "CPU"),
        ("VM.Config.Memory", "RAM"),
        ("VM.Config.Options", "Name and description"),
        ("VM.Config.Network", "Network interfaces")
    };

    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        var inventory = await ListVmsAsync(ct);
        var root = await ReadDataAsync("api2/json/access/permissions?path=%2Fvms", ct);
        var inherited = root.TryGetProperty("/vms", out var rootRights)
            ? RequiredVmPermissions.Where(p => rootRights.TryGetProperty(p.Privilege, out var value) &&
                (value.ValueKind == System.Text.Json.JsonValueKind.True || value.ToString() == "1")).Select(p => p.Privilege).ToHashSet()
            : new HashSet<string>();
        using var limit = new SemaphoreSlim(4);
        var results = await Task.WhenAll(inventory.Select(async vm =>
        {
            await limit.WaitAsync(ct);
            try
            {
                var path = "/vms/" + vm.Id;
                var data = await ReadDataAsync("api2/json/access/permissions?path=" + Uri.EscapeDataString(path), ct);
                var found = data.TryGetProperty(path, out var rights);
                // A privilege with value 0 is granted here but does not propagate.
                var missing = RequiredVmPermissions.Where(p => !found || !rights.TryGetProperty(p.Privilege, out _)).ToArray();
                return (Vm: vm, Missing: missing);
            }
            finally { limit.Release(); }
        }));
        var lines = new List<string> { "Connection successful. API token accepted.", "", "Access to all current and future VMs:" };
        foreach (var p in RequiredVmPermissions)
            lines.Add($"{(inherited.Contains(p.Privilege) ? "Available" : "Not enabled for all VMs")}: {p.Feature} ({p.Privilege})");
        lines.Add("");
        lines.Add($"Visible VMs (templates excluded): {results.Length}");
        if (results.Length == 0)
            lines.Add("No VMs were returned. This server may have no VMs, or this connection may not have permission to see them.");
        else
        {
            lines.Add($"Full application permissions: {results.Count(r => r.Missing.Length == 0)} of {results.Length} visible VMs.");
            foreach (var result in results)
                lines.Add($"VM {result.Vm.Id} ({result.Vm.Name}): " + (result.Missing.Length == 0 ? "All required permissions available." :
                    "Missing: " + string.Join(", ", result.Missing.Select(p => $"{p.Feature} [{p.Privilege}]"))));
        }
        lines.Add("");
        try {
            var bridgeRights=await ReadDataAsync("api2/json/access/permissions?path="+Uri.EscapeDataString(SetupPlan.BridgePath),ct);
            bool bridgeUse=bridgeRights.TryGetProperty(SetupPlan.BridgePath,out var networkRights)&&networkRights.TryGetProperty("SDN.Use",out var inheritedUse)&&inheritedUse.ToString() is "1" or "True";
            lines.Add(bridgeUse?"Network selection: permission to use networks in all zones is available.":"Network selection: some networks may be missing. Try Manage servers > Repair permissions. For manually configured accounts, ask your administrator to allow network access (SDN.Use at /sdn/zones for the user and token, including child entries).");
        } catch(System.Net.Http.HttpRequestException) {lines.Add("Network permissions could not be checked.");}
        lines.Add("Networks also need to be set up and available on the server running the VM.");
        lines.Add("Access can be limited to individual VMs; access to all VMs is optional. This check covers only VMs visible to this connection. It checks login and permissions, not console connections, and does not change any VM.");
        return string.Join(Environment.NewLine, lines);
    }
}



