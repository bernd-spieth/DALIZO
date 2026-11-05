// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ProxmoxSpice;

public sealed record RepairGrant(string Path,bool Token);
public sealed record AccessRepairPreview(string TokenId,string User,string Role,string[] Paths,string[] MissingPrivileges,RepairGrant[] Grants,string Fingerprint,bool Shared)
{
    public bool HasChanges=>MissingPrivileges.Length>0||Grants.Length>0;
    public string Description=>$"Token: {TokenId}\nVM scope: {string.Join(", ",Paths.Where(p=>p!=SetupPlan.BridgePath))}\n"
        +"Network scope: /sdn/zones (all zones, inherited SDN.Use).\n"
        +(MissingPrivileges.Length==0?"Role privileges are complete.":"Add role privileges: "+string.Join(", ",MissingPrivileges))+"\n"
        +(Grants.Length==0?"Access assignments are complete.":"Add or enable inheritance:\n"+string.Join("\n",Grants.Select(g=>(g.Token?"Token: ":"User: ")+g.Path)))
        +(Shared?"\nOther tokens share this account/role. Added role privileges and user access also apply to their existing scopes.":"");
}
public sealed partial class ProxmoxSetup
{
    public async Task<AccessRepairPreview> InspectRepairAsync(string tokenId)
    {
        if(!TryManagedAccess(tokenId,out var user,out var role))throw new InvalidOperationException("Automatic repair supports DALIZO-generated accounts only. Manually configured accounts must be reviewed in Proxmox; no changes were made.");
        await RequireAccessAudit();
        var users=await Request(HttpMethod.Get,"access/users?full=1");
        var roles=await Request(HttpMethod.Get,"access/roles");
        var acl=await Request(HttpMethod.Get,"access/acl");
        var u=users.EnumerateArray().FirstOrDefault(x=>Text(x,"userid")==user);
        var r=roles.EnumerateArray().FirstOrDefault(x=>Text(x,"roleid")==role);
        if(u.ValueKind!=JsonValueKind.Object||Text(u,"comment")!="DALIZO dedicated API account"||Text(u,"enable") is not ("1" or "True")||Text(u,"groups") is not ("" or "[]"))
            throw new InvalidOperationException("The DALIZO account was removed, disabled or changed. Ask your Proxmox administrator to check it. Nothing was changed.");
        if(r.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("The saved permission settings are missing in Proxmox. DALIZO cannot tell which VMs this connection should use. Ask your administrator to check the account; nothing was changed.");
        var current=Text(r,"privs").Split(new[]{' ',','},StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if(!current.IsSubsetOf(SetupPlan.RolePrivileges))throw new InvalidOperationException("This account has custom permissions. Ask your Proxmox administrator to check them; automatic repair has made no changes.");
        var tokens=await Request(HttpMethod.Get,"access/users/"+Uri.EscapeDataString(user)+"/token");
        var t=tokens.EnumerateArray().FirstOrDefault(x=>Text(x,"tokenid")==tokenId.Split('!')[1]);
        if(t.ValueKind!=JsonValueKind.Object||Text(t,"comment")!="DALIZO client"||Text(t,"privsep") is not ("1" or "True"))
            throw new InvalidOperationException("The login key was removed or its security settings differ from DALIZO’s setup. Ask your Proxmox administrator to check it. Nothing was changed.");
        bool VmPath(string p)=>Regex.IsMatch(p,@"^/vms(?:/[1-9][0-9]*)?$");
        var entries=acl.EnumerateArray().ToArray();
        var own=entries.Where(a=>Text(a,"type")=="user"&&Text(a,"ugid")==user||Text(a,"type")=="token"&&Text(a,"ugid")==tokenId).ToArray();
        if(own.Any(a=>Text(a,"roleid")!=role||!(VmPath(Text(a,"path"))||Text(a,"path")==SetupPlan.BridgePath||Text(a,"path")==SetupPlan.LegacyBridgePath)))
            throw new InvalidOperationException("The permissions have been customized. DALIZO cannot safely determine which access to repair. Ask your Proxmox administrator to check them. Nothing was changed.");
        // Never infer an all-VM scope from the user's broader permissions or visible VM inventory.
        var vmPaths=own.Where(a=>Text(a,"type")=="token"&&VmPath(Text(a,"path"))).Select(a=>Text(a,"path")).Distinct().Order().ToArray();
        if(vmPaths.Length==0)throw new InvalidOperationException("No VM selection is recorded for this login key in Proxmox. Ask your administrator to assign the intended VMs before trying again. Nothing was changed.");
        if(entries.Any(a=>Text(a,"roleid")==role&&!(Text(a,"type")=="user"&&Text(a,"ugid")==user)&&!(Text(a,"type")=="token"&&Text(a,"ugid").StartsWith(user+"!",StringComparison.Ordinal))))
            throw new InvalidOperationException("These permissions are also used by another account. Ask your Proxmox administrator to review the changes. Nothing was changed.");
        var paths=vmPaths.Append(SetupPlan.BridgePath).ToArray();
        var grants=new List<RepairGrant>();
        foreach(var path in paths)foreach(bool isToken in new[]{false,true})
            if(!own.Any(a=>Text(a,"path")==path&&Text(a,"type")== (isToken?"token":"user")&&Text(a,"propagate") is "1" or "True"))grants.Add(new(path,isToken));
        // Entire audited state is checked again after review, before the first write.
        string Canonical(JsonElement value)=>value.ValueKind switch {
            JsonValueKind.Array=>"["+string.Join(",",value.EnumerateArray().Select(Canonical).Order(StringComparer.Ordinal))+"]",
            JsonValueKind.Object=>"{"+string.Join(",",value.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal).Select(p=>JsonSerializer.Serialize(p.Name)+":"+Canonical(p.Value)))+"}",
            _=>value.GetRawText()
        };
        var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(users)+Canonical(roles)+Canonical(acl)+Canonical(tokens))));
        return new(tokenId,user,role,paths,SetupPlan.RolePrivileges.Where(p=>!current.Contains(p)).ToArray(),grants.ToArray(),fingerprint,tokens.GetArrayLength()>1);
    }
    public async Task RepairAccessAsync(AccessRepairPreview approved,Func<string[],Task> verify)
    {
        var fresh=await InspectRepairAsync(approved.TokenId);
        if(JsonSerializer.Serialize(fresh)!=JsonSerializer.Serialize(approved))throw new InvalidOperationException("Permissions changed after you reviewed them. Run the check again before continuing.");
        var done=new List<string>();
        try {
            if(fresh.MissingPrivileges.Length>0){await Request(HttpMethod.Put,"access/roles/"+Uri.EscapeDataString(fresh.Role),new(){["privs"]=string.Join(" ",fresh.MissingPrivileges),["append"]="1"});done.Add("role privileges");}
            foreach(var grant in fresh.Grants){await Request(HttpMethod.Put,"access/acl",new(){["path"]=grant.Path,[grant.Token?"tokens":"users"]=grant.Token?fresh.TokenId:fresh.User,["roles"]=fresh.Role,["propagate"]="1"});done.Add((grant.Token?"token ":"user ")+grant.Path);}
            await verify(fresh.Paths);
        }catch {
            throw new InvalidOperationException("Repair or token verification did not finish. Confirmed additions: "+(done.Count==0?"none":string.Join(", ",done))+". Additions were retained; the token and profile were not replaced. A timed-out request may have completed. Inspect again to retry safely.");
        }
    }
}
