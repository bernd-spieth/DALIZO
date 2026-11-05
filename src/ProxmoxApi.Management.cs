// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Text.Json;
namespace ProxmoxSpice;

public sealed record VmRuntime(string Status, string QmpStatus, long Uptime)
{
    public string Label => Status == "stopped" ? "Stopped" : QmpStatus switch
    {
        "shutdown" => "Guest shut down",
        "paused" or "suspended" => "Paused",
        "" or "running" => Status == "running" ? "Running" : "Unknown",
        _ => "Not running"
    };
}
public sealed record ProxmoxJob(string Node, string Upid);
public sealed record JobStatus(string Status, string? ExitStatus);
public sealed record VmConsoleInfo(bool IsTemplate, string DisplayType)
{
    public string[] SerialPorts {get;init;}=Array.Empty<string>();
    public bool HasSpice => DisplayType is "qxl" or "qxl2" or "qxl3" or "qxl4";
    public string Resolve(string preference)
    {
        if(IsTemplate)throw new InvalidOperationException("Templates cannot be connected.");
        if(preference=="Serial terminal")return SerialPorts.Length>0?preference:throw new InvalidOperationException("No socket-backed serial port is configured. Add a serial socket in Proxmox and enable a serial login inside Linux.");
        if(DisplayType=="none")throw new InvalidOperationException("No display is configured for this VM.");
        if(preference=="SPICE" && !HasSpice)throw new InvalidOperationException("SPICE is not configured for this VM. Use Edit name / console to select Auto or noVNC.");
        return preference=="Auto" ? (HasSpice ? "SPICE" : "noVNC") : preference;
    }
    public static VmConsoleInfo Parse(JsonElement data)
    {
        bool template=data.TryGetProperty("template",out var t) && (t.ValueKind==JsonValueKind.True || t.ToString()=="1");
        var raw=data.TryGetProperty("vga",out var v) ? v.GetString() ?? "" : "";
        var type=raw.Split(',').Select(p=>p.Trim()).FirstOrDefault(p=>p.StartsWith("type="))?[5..]
            ?? raw.Split(',').Select(p=>p.Trim()).FirstOrDefault(p=>p.Length>0&&!p.Contains('=')) ?? "std";
        return new(template,type){SerialPorts=Enumerable.Range(0,4).Select(i=>"serial"+i).Where(k=>data.TryGetProperty(k,out var value)&&value.GetString()=="socket").ToArray()};
    }
}
public sealed record VmConfiguration(string Digest, Dictionary<string, string> Values)
{
    public string Get(string key, string fallback = "") => Values.GetValueOrDefault(key, fallback);
    public int Number(string key, int fallback) => int.TryParse(Get(key), out var number) ? number : fallback;
    public int MemoryMiB
    {
        get
        {
            var raw = Get("memory", "512"); if (int.TryParse(raw, out var direct)) return direct;
            var memoryPart = raw.Split(',').FirstOrDefault(p => p.StartsWith("current=", StringComparison.Ordinal));
            return memoryPart != null && int.TryParse(memoryPart[8..], out var size) ? size : throw new InvalidOperationException("This editor does not support the RAM format.");
        }
    }
    public string MemoryValue(int size)
    {
        var raw = Get("memory", "512"); if (int.TryParse(raw, out _)) return size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(",", raw.Split(',').Select(part => part.StartsWith("current=", StringComparison.Ordinal) ? "current=" + size : part));
    }
    public Dictionary<string, string> Changes(string name, string description, int cores, int sockets, int memory)
    {
        if (cores < 1 || sockets < 1 || memory < 16) throw new ArgumentException("CPU values must be positive; RAM must be at least 16 MiB.");
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace) || name.Any(char.IsControl)) throw new ArgumentException("Enter a VM name without spaces or control characters.");
        if (Number("balloon", 0) > memory) throw new ArgumentException("RAM is below the existing ballooning minimum. Adjust it in Proxmox first.");
        if (Number("vcpus", 0) > (long)cores * sockets) throw new ArgumentException("The CPU count is below the configured hotplug vCPU count. Adjust it in Proxmox first.");
        var result = new Dictionary<string, string>();
        if (name != Get("name")) result["name"] = name;
        if (description != Get("description")) result["description"] = description;
        if (cores != Number("cores", 1)) result["cores"] = cores.ToString();
        if (sockets != Number("sockets", 1)) result["sockets"] = sockets.ToString();
        if (memory != MemoryMiB) result["memory"] = MemoryValue(memory);
        return result;
    }
}
public sealed partial class ProxmoxApi
{
    static string VmPath(Vm vm) => $"api2/json/nodes/{Uri.EscapeDataString(vm.Node)}/qemu/{vm.Id}";
    async Task<JsonElement> ReadDataAsync(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct); await CheckAsync(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); return doc.RootElement.GetProperty("data").Clone();
    }
    public async Task<Vm> ResolveVmAsync(int id, CancellationToken ct = default) => (await ListVmsAsync(ct)).FirstOrDefault(v => v.Id == id)
        ?? throw new InvalidOperationException("The VM is not visible. Check the VM ID and token permissions.");
    public async Task<VmRuntime> GetRuntimeAsync(Vm vm, CancellationToken ct = default)
    {
        var data = await ReadDataAsync(VmPath(vm) + "/status/current", ct);
        return new(data.GetProperty("status").GetString() ?? "unknown", data.TryGetProperty("qmpstatus", out var q) ? q.GetString() ?? "" : "", data.TryGetProperty("uptime", out var u) && u.TryGetInt64(out var seconds) ? seconds : 0);
    }
    public async Task<VmConfiguration> GetConfigurationAsync(Vm vm, CancellationToken ct = default, bool current = false)
    {
        var data = await ReadDataAsync(VmPath(vm) + "/config" + (current ? "?current=1" : ""), ct);
        var values = data.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText());
        return new(values.GetValueOrDefault("digest", ""), values);
    }
    public async Task<VmConsoleInfo> GetConsoleInfoAsync(Vm vm, CancellationToken ct = default)
        => VmConsoleInfo.Parse(await ReadDataAsync(VmPath(vm)+"/config?current=1",ct));
    public async Task<bool> CanEditNetworkAsync(int id,CancellationToken ct=default)
    {
        var path="/vms/"+id;
        var data=await ReadDataAsync("api2/json/access/permissions?path="+Uri.EscapeDataString(path),ct);
        return data.TryGetProperty(path,out var rights)&&rights.TryGetProperty("VM.Config.Network",out _);
    }
    public async Task<bool> CanUseVmAsync(int id,CancellationToken ct=default)
    {
        if(id<=0)throw new ArgumentException("Invalid VM ID.");
        var path="/vms/"+id;
        var permissions=await ReadDataAsync("api2/json/access/permissions?path="+Uri.EscapeDataString(path),ct);
        if(!permissions.TryGetProperty(path,out var rights))return false;
        // The values indicate propagation, not whether the privilege is granted.
        return new[]{"VM.Console","VM.PowerMgmt","VM.Config.CPU","VM.Config.Memory","VM.Config.Options","VM.Config.Network"}.Any(p=>rights.TryGetProperty(p,out _));
    }
    public async Task<ProxmoxJob?> PowerAsync(Vm vm, string action, CancellationToken ct = default)
    {
        if (action is not ("start" or "shutdown" or "reboot" or "stop")) throw new ArgumentException("Unknown VM action.");
        // Never silently escalate a graceful shutdown to a forced stop.
        var args = new Dictionary<string, string>(); if (action == "shutdown") args["forceStop"] = "0";
        return await PostJobAsync(VmPath(vm) + "/status/" + action, args, vm.Node, ct) ?? throw new InvalidOperationException("Server task not confirmed. Check its status before submitting the action again.");
    }
    public Task<ProxmoxJob?> UpdateConfigurationAsync(Vm vm, string digest, IReadOnlyDictionary<string, string> changes, CancellationToken ct = default)
    {
        if (digest.Length != 40 || !digest.All(Uri.IsHexDigit)) throw new ArgumentException("Configuration version is missing. Reopen the dialog.");
        if (changes.Count == 0) throw new ArgumentException("No changes.");
        if (changes.Keys.Any(k => !VmNetwork.IsKey(k) && k is not ("name" or "description" or "cores" or "sockets" or "memory"))) throw new ArgumentException("Unsupported configuration option.");
        var fields = changes.ToDictionary(p => p.Key, p => p.Value); fields["digest"] = digest;
        return PostJobAsync(VmPath(vm) + "/config", fields, vm.Node, ct);
    }
    async Task<ProxmoxJob?> PostJobAsync(string path, Dictionary<string, string> fields, string fallbackNode, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        using var response = await http.SendAsync(request, ct); await CheckAsync(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var data = doc.RootElement.GetProperty("data");
        if (data.ValueKind == JsonValueKind.Null) return null;
        var upid = data.GetString(); if (string.IsNullOrWhiteSpace(upid) || !upid.StartsWith("UPID:")) throw new InvalidOperationException("Server task confirmation was ambiguous. Check its status before submitting the action again.");
        var parts = upid.Split(':'); return new(parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : fallbackNode, upid);
    }
    public async Task<JobStatus> GetJobAsync(ProxmoxJob job, CancellationToken ct = default)
    {
        var data = await ReadDataAsync($"api2/json/nodes/{Uri.EscapeDataString(job.Node)}/tasks/{Uri.EscapeDataString(job.Upid)}/status", ct);
        return new(data.GetProperty("status").GetString() ?? "unknown", data.TryGetProperty("exitstatus", out var e) ? e.GetString() : null);
    }
}




