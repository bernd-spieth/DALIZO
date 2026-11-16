// SPDX-License-Identifier: GPL-3.0-only
namespace ProxmoxSpice;
public sealed partial class ProxmoxApi
{
    internal static readonly string[] DiskStoragePrivileges={"Datastore.Audit","Datastore.AllocateSpace"};
    public async Task VerifySetupPermissionsAsync(IEnumerable<string> paths)
    {
        foreach(var path in paths) {
            var result=await ReadDataAsync("api2/json/access/permissions?path="+Uri.EscapeDataString(path),CancellationToken.None);
            var required=ProxmoxSetup.IsStoragePath(path)?DiskStoragePrivileges:path==SetupPlan.BridgePath?new[]{"SDN.Use"}:RequiredVmPermissions.Select(p=>p.Privilege);
            if(!result.TryGetProperty(path,out var rights) || required.Any(p=>!rights.TryGetProperty(p,out var value)
                || ((path=="/vms"||path==SetupPlan.BridgePath) && value.ToString() is not ("1" or "True"))))
                throw new InvalidOperationException("The generated token does not have all requested permissions at "+path+".");
        }
        await ListVmsAsync();
    }
}
