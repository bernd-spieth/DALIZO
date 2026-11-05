// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text.Json;
namespace ProxmoxSpice;

public sealed record ServerProfile(Guid Id, string Name, string Address, string TokenId, string Proxy, string? ProtectedSecret);
public sealed record SavedVm(Guid Id, Guid ServerId, int VmId, string Name, string ConsoleType = "Auto", bool ExplicitConsole = false, bool ClipboardEnabled = true, bool AutoResolution = true, string SerialPort = "serial0", string Folder = "", bool ImageClipboardEnabled = false, bool FileTransferEnabled = false);
public sealed record WorkspaceData(int Version, List<ServerProfile> Servers, List<SavedVm> Vms)
{
    public static WorkspaceData Empty() => new(1, new(), new());
}
public sealed class ProfileStore(string file)
{
    public string FilePath { get; } = Path.GetFullPath(file);
    public WorkspaceData Load()
    {
        if (!File.Exists(FilePath)) return WorkspaceData.Empty();
        var data = JsonSerializer.Deserialize<WorkspaceData>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException("The profile file is empty or invalid.");
        // Earlier versions assigned SPICE indiscriminately. Preserve noVNC and new explicit overrides.
        data = data with { Vms = data.Vms?.Select(v => v is {ConsoleType:"SPICE", ExplicitConsole:false} ? v with { ConsoleType="Auto" } : v!).ToList()! };
        Validate(data);
        return data;
    }
    public void Save(WorkspaceData data)
    {
        Validate(data);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Validate(WorkspaceData data)
    {
        if (data.Version != 1 || data.Servers == null || data.Vms == null)
            throw new InvalidDataException("Unknown or damaged profile file; the file will not be overwritten.");
        if (data.Servers.Any(s => s == null) || data.Vms.Any(v => v == null))
            throw new InvalidDataException("Invalid profile entry.");
        if (data.Servers.Select(s => s.Id).Distinct().Count() != data.Servers.Count || data.Vms.Select(v => v.Id).Distinct().Count() != data.Vms.Count)
            throw new InvalidDataException("Duplicate profile ID.");
        foreach (var s in data.Servers)
        {
            if (s.Id == Guid.Empty || string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.TokenId) || s.Proxy == null)
                throw new InvalidDataException("Server name and token ID are required.");
            using var api = new ProxmoxApi(s.Address);
            api.UseToken(s.TokenId, "validation-only");
            if (s.Proxy.Length > 0 && Uri.CheckHostName(s.Proxy) == UriHostNameType.Unknown)
                throw new InvalidDataException("Enter a SPICE proxy hostname or IP address without a port.");
        }
        foreach (var v in data.Vms)
            if (v.Id == Guid.Empty || string.IsNullOrWhiteSpace(v.Name) || v.VmId <= 0 || !data.Servers.Any(s => s.Id == v.ServerId) || (v.ConsoleType is not ("Auto" or "SPICE" or "noVNC" or "Serial terminal") || v.SerialPort is not ("serial0" or "serial1" or "serial2" or "serial3")))
                throw new InvalidDataException("A VM needs a name, a positive VM ID and an existing server profile.");
        if(data.Vms.Any(v=>v.Folder==null || v.Folder.Length>80 || v.Folder!=v.Folder.Trim() || v.Folder.Any(char.IsControl) || v.Folder.Equals("Ungrouped",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Folder names must be at most 80 characters; Ungrouped is reserved.");
        if (data.Vms.Select(v => (v.ServerId, v.VmId)).Distinct().Count() != data.Vms.Count)
            throw new InvalidDataException("This VM already exists in the server profile.");
    }
}




