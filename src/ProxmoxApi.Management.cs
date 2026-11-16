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
    public string AudioDevice {get;init;}="";
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
        return new(template,type){AudioDevice=data.TryGetProperty("audio0",out var audio)?audio.GetString()??"":"",
            SerialPorts=Enumerable.Range(0,4).Select(i=>"serial"+i).Where(k=>data.TryGetProperty(k,out var value)&&value.GetString()=="socket").ToArray()};
    }
}
public sealed record VmConfiguration(string Digest, Dictionary<string, string> Values)
{
    public const string SpiceAudio="device=ich9-intel-hda,driver=spice";
    static bool IsAgentFlag(string part)=>!part.Contains('=') || part.StartsWith("enabled=",StringComparison.Ordinal);
    static string[] AgentParts(string raw)=>raw.Split(',',StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool AgentEnabled=>AgentParts(Get("agent")).Any(p=>IsAgentFlag(p) && (p.Split('=').Last() is "1" or "true" or "yes" or "on"));
    public string AgentValue(bool enabled)=>string.Join(",",AgentParts(Get("agent")).Where(p=>!IsAgentFlag(p)).Prepend(enabled?"enabled=1":"enabled=0"));
    public static bool IsSerialSocketSlot(string key)=>key is "serial0" or "serial1" or "serial2" or "serial3";
    public bool Matches(string key,string value){
        if(key is "hotplug" or "startup" or "spice_enhancements" or "smbios1")
            return Get(key).Split(',').Order().SequenceEqual(value.Split(',').Order());
        if(IsUsb(key) && value=="spice")return Get(key).Split(',')[0] is "spice" or "host=spice";
        if(IsDrive(key)){
            var actual=Get(key).Split(',');
            var requested=value.Split(',');
            var volume=requested[0].Split(':');
            if(volume.Length==2 && int.TryParse(volume[1],out _)){
                return actual[0].StartsWith(volume[0]+":",StringComparison.Ordinal) && actual[0]!=requested[0]
                    && requested.Skip(1).All(actual.Contains);
            }
            return requested.All(actual.Contains);
        }
        if(key=="agent")return AgentParts(AgentValue(AgentEnabled)).Order().SequenceEqual(AgentParts(value).Order());
        if(key=="delete")return value.Split(',').All(k=>!Values.ContainsKey(k));
        if(VmNetwork.IsKey(key))return VmNetwork.Equivalent(Get(key),value);
        if(key=="audio0" && value==SpiceAudio){
            var parts=Get(key).Split(',');
            return (parts.Contains("ich9-intel-hda") || parts.Contains("device=ich9-intel-hda"))
                && (parts.Contains("driver=spice") || !parts.Any(p=>p.StartsWith("driver=")));
        }
        return Get(key)==value;
    }
    public string Get(string key, string fallback = "") => Values.GetValueOrDefault(key, fallback);
    public static bool IsDrive(string key)=>key is "efidisk0" or "tpmstate0" ||
        System.Text.RegularExpressions.Regex.IsMatch(key,@"^(ide[0-3]|sata[0-5]|scsi([0-9]|[12][0-9]|30)|virtio([0-9]|1[0-5]))$");
    public static bool IsUnused(string key)=>System.Text.RegularExpressions.Regex.IsMatch(key,@"^unused[0-9]+$");
    public static bool IsUsb(string key)=>System.Text.RegularExpressions.Regex.IsMatch(key,@"^usb([0-9]|1[0-3])$");
    public int UsbSlots{
        get{
            var version=System.Text.RegularExpressions.Regex.Match(Get("machine"),@"-(\d+\.\d+)");
            bool modern=!version.Success || Version.Parse(version.Groups[1].Value)>=new Version(7,1);
            string os=Get("ostype");
            return modern && (os=="l26" || os.StartsWith("win") && int.TryParse(os[3..],out int win) && win>7)?14:5;
        }
    }
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
    public bool BalloonEnabled=>Number("balloon",-1)!=0;
    public string MemorySummary=>MemoryMiB+" MiB · "+(BalloonEnabled?"Ballooning on · Minimum "+Number("balloon",MemoryMiB)+" MiB":"Ballooning off");
    public Dictionary<string, string> Changes(string name, string description, int cores, int sockets, int memory,int? balloon=null)
    {
        if (cores < 1 || sockets < 1 || memory < 16) throw new ArgumentException("CPU values must be positive; RAM must be at least 16 MiB.");
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace) || name.Any(char.IsControl)) throw new ArgumentException("Enter a VM name without spaces or control characters.");
        // -1 follows configured RAM (Proxmox default), 0 disables the device.
        int previous=Number("balloon",-1), requested=balloon??previous;
        if(requested < -1 || requested > memory)throw new ArgumentException("Minimum memory must not exceed configured RAM.");
        if (Number("vcpus", 0) > (long)cores * sockets) throw new ArgumentException("The CPU count is below the configured hotplug vCPU count. Adjust it in Proxmox first.");
        var result = new Dictionary<string, string>();
        if (name != Get("name")) result["name"] = name;
        if (description != Get("description")) result["description"] = description;
        if (cores != Number("cores", 1)) result["cores"] = cores.ToString();
        if (sockets != Number("sockets", 1)) result["sockets"] = sockets.ToString();
        if (memory != MemoryMiB) result["memory"] = MemoryValue(memory);
        if(requested!=previous){
            var remove=new List<string>();
            if(requested==-1)remove.Add("balloon");else result["balloon"]=requested.ToString();
            if((requested<=0 || requested==memory) && Values.ContainsKey("shares"))remove.Add("shares");
            if(remove.Count>0)result["delete"]=string.Join(",",remove);
        }
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
    public async Task<string[]> MissingStoragePrivilegesAsync(string store,CancellationToken ct)
    {
        string path="/storage/"+store;
        var data=await ReadDataAsync("api2/json/access/permissions?path="+Uri.EscapeDataString(path),ct);
        return data.TryGetProperty(path,out var rights)
            ? DiskStoragePrivileges.Where(p=>!rights.TryGetProperty(p,out _)).ToArray()
            : DiskStoragePrivileges;
    }
    public async Task<string[]> StorageChoicesAsync(string node,string content,CancellationToken ct){
        var path="api2/json/nodes/"+Uri.EscapeDataString(node)+"/storage";
        var entries=await ReadDataAsync(path+"?enabled=1&content="+content,ct);
        var stores=entries.EnumerateArray().Where(s=>s.GetProperty("active").ToString() is "1" or "True")
            .Select(s=>s.GetProperty("storage").GetString()!).Order().ToArray();
        if(content!="iso")return stores;
        var images=new List<string>();
        foreach(string store in stores){
            var files=await ReadDataAsync(path+"/"+Uri.EscapeDataString(store)+"/content?content=iso",ct);
            images.AddRange(files.EnumerateArray().Select(f=>f.GetProperty("volid").GetString()!));
        }
        return images.Order().ToArray();
    }
    public async Task<string[]> UsbMappingsAsync(string node,CancellationToken ct){
        var data=await ReadDataAsync("api2/json/cluster/mapping/usb?check-node="+Uri.EscapeDataString(node),ct);
        return data.EnumerateArray().Where(m=>m.TryGetProperty("map",out var mappings) && mappings.EnumerateArray()
            .Any(entry=>(entry.GetString()??"").Split(',').Contains("node="+node)))
            .Select(m=>m.GetProperty("id").GetString()!).Order().ToArray();
    }
    public async Task<bool> CanEditStartupAsync(CancellationToken ct){
        var data=await ReadDataAsync("api2/json/access/permissions?path=%2F",ct);
        return data.TryGetProperty("/",out var rights) && rights.TryGetProperty("Sys.Modify",out _);
    }
    public async Task<(bool Network,bool Hardware,bool Options,bool Memory,bool Disk,bool Cdrom,bool Power)> GetDevicePermissionsAsync(int id,CancellationToken ct=default)
    {
        var path="/vms/"+id;
        var data=await ReadDataAsync("api2/json/access/permissions?path="+Uri.EscapeDataString(path),ct);
        if(!data.TryGetProperty(path,out var rights))return (false,false,false,false,false,false,false);
        return (rights.TryGetProperty("VM.Config.Network",out _),rights.TryGetProperty("VM.Config.HWType",out _),rights.TryGetProperty("VM.Config.Options",out _),rights.TryGetProperty("VM.Config.Memory",out _),rights.TryGetProperty("VM.Config.Disk",out _),rights.TryGetProperty("VM.Config.CDROM",out _),rights.TryGetProperty("VM.PowerMgmt",out _));
    }
    public async Task<bool> CanEditNetworkAsync(int id,CancellationToken ct=default)=>(await GetDevicePermissionsAsync(id,ct)).Network;
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
        foreach(var (key,value) in changes){
            if(VmNetwork.IsKey(key) || key is "name" or "description" or "cores" or "sockets" or "memory" or "agent")continue;
            if(key=="audio0" && value==VmConfiguration.SpiceAudio)continue;
            if(key is "boot" or "startup" or "onboot" or "protection" or "ostype" or "tablet" or "hotplug" or "acpi" or "kvm" or "freeze" or "localtime" or "startdate" or "smbios1" or "spice_enhancements")continue;
            if(VmConfiguration.IsDrive(key))continue;
            if(VmConfiguration.IsUsb(key) && (value=="spice" || System.Text.RegularExpressions.Regex.IsMatch(value,@"^mapping=[a-zA-Z0-9_.-]+$")))continue;
            if(key=="balloon" && int.TryParse(value,out int minimum) && minimum>=0)continue;
            if(VmConfiguration.IsSerialSocketSlot(key) && value=="socket")continue;
            if(key=="delete" && value.Split(',').All(k=>(k is "audio0" or "balloon" or "shares" or "startup" or "startdate" or "smbios1" or "localtime" || VmConfiguration.IsSerialSocketSlot(k) || VmNetwork.IsKey(k) || VmConfiguration.IsDrive(k) || VmConfiguration.IsUnused(k) || VmConfiguration.IsUsb(k)) && !changes.ContainsKey(k)))continue;
            throw new ArgumentException("Unsupported configuration option.");
        }
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
