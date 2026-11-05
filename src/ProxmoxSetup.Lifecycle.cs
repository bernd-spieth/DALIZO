// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ProxmoxSpice;

public sealed record CleanupPreview(string User,string Role,string TokenId,bool UserExists,bool RoleExists,bool TokenExists,bool RemoveAccount,string[] TokenPaths,string[] UserPaths)
{
    public string Description=>$"Token: {TokenId}\nAccount: {User}\nRole: {Role}\n"+(RemoveAccount?"Remove the token, its access assignments, the dedicated account and its role.":"Remove this token and its access assignments only. Keep the account and role because they are shared or modified.");
}
public sealed partial class ProxmoxSetup
{
    public static bool TryManagedAccess(string tokenId,out string user,out string role)
    {
        var m=Regex.Match(tokenId,@"^dalizo-([a-f0-9]{12})@pve!client(?:-[a-f0-9]{12})?$");
        user=m.Success?"dalizo-"+m.Groups[1].Value+"@pve":"";role=m.Success?"DALIZO_"+m.Groups[1].Value:"";return m.Success;
    }
    static string Text(JsonElement e,string name)=>e.TryGetProperty(name,out var p)?p.ToString():"";
    async Task RequireAccessAudit()
    {
        if(!http.DefaultRequestHeaders.Contains("CSRFPreventionToken"))throw new InvalidOperationException("Administrator sign-in is required.");
        var permissions=await Request(HttpMethod.Get,"access/permissions?path=%2F");
        if(!permissions.TryGetProperty("/",out var rights)||new[]{"Sys.Audit","Sys.Modify","User.Modify","Permissions.Modify"}.Any(p=>!rights.TryGetProperty(p,out var v)||v.ToString() is not ("1" or "True")))
            throw new InvalidOperationException("This administrator account cannot check and manage all required permissions. Sign in with a full administrator account, such as root@pam.");
    }
    bool ExpectedRole(JsonElement role){var privileges=Text(role,"privs").Split(new[]{' ',','},StringSplitOptions.RemoveEmptyEntries).ToHashSet();return privileges.SetEquals(SetupPlan.RolePrivileges)||privileges.SetEquals(ProxmoxApi.RequiredVmPermissions.Select(p=>p.Privilege));}
    public async Task<CleanupPreview> InspectCleanupAsync(string tokenId)
    {
        if(!TryManagedAccess(tokenId,out var user,out var role))throw new InvalidOperationException("This is not a recognized DALIZO-generated access identity. Remove only the local profile, or manage access in Proxmox.");
        await RequireAccessAudit();
        var users=await Request(HttpMethod.Get,"access/users?full=1");
        var roles=await Request(HttpMethod.Get,"access/roles");
        var acl=await Request(HttpMethod.Get,"access/acl");
        var u=users.EnumerateArray().FirstOrDefault(x=>Text(x,"userid")==user);
        var r=roles.EnumerateArray().FirstOrDefault(x=>Text(x,"roleid")==role);
        bool ue=u.ValueKind==JsonValueKind.Object,re=r.ValueKind==JsonValueKind.Object;
        if(ue&&Text(u,"comment")!="DALIZO dedicated API account")throw new InvalidOperationException("The account is not marked as a DALIZO dedicated account. No access will be removed.");
        var tokens=ue?await Request(HttpMethod.Get,"access/users/"+Uri.EscapeDataString(user)+"/token"):JsonSerializer.SerializeToElement(Array.Empty<object>());
        string token=tokenId.Split('!')[1];
        var t=tokens.EnumerateArray().FirstOrDefault(x=>Text(x,"tokenid")==token);
        bool te=t.ValueKind==JsonValueKind.Object;
        if(te&&Text(t,"comment")!="DALIZO client")throw new InvalidOperationException("The token is not marked as a DALIZO client token. Nothing was changed.");
        var entries=acl.EnumerateArray().ToArray();
        bool Ours(JsonElement a,string subject,string type)=>Text(a,"ugid")==subject&&Text(a,"type")==type;
        bool Expected(JsonElement a)=>Text(a,"roleid")==role&&(Text(a,"path")==SetupPlan.BridgePath||Text(a,"path")==SetupPlan.LegacyBridgePath||Regex.IsMatch(Text(a,"path"),@"^/vms(?:/[1-9][0-9]*)?$"));
        var ta=entries.Where(a=>Ours(a,tokenId,"token")).ToArray();
        if(ta.Any(a=>!Expected(a)))throw new InvalidOperationException("This connection’s permissions have been changed outside the expected setup. Ask your Proxmox administrator to check them before removal. Nothing was removed.");
        var ua=entries.Where(a=>Ours(a,user,"user")).ToArray();
        bool groups=ue&&Text(u,"groups") is not ("" or "[]");
        bool shared=tokens.EnumerateArray().Any(x=>Text(x,"tokenid")!=token)||groups||ua.Any(a=>!Expected(a))
            ||(re&&!ExpectedRole(r))||entries.Any(a=>Text(a,"roleid")==role&&!Ours(a,user,"user")&&!Ours(a,tokenId,"token"));
        return new(user,role,tokenId,ue,re,te,!shared,ta.Select(a=>Text(a,"path")).Distinct().Order().ToArray(),ua.Select(a=>Text(a,"path")).Distinct().Order().ToArray());
    }
    public async Task<string> CleanupAsync(CleanupPreview approved)
    {
        var current=await InspectCleanupAsync(approved.TokenId);
        if(JsonSerializer.Serialize(current)!=JsonSerializer.Serialize(approved))throw new InvalidOperationException("Access changed since review. Inspect it again before removing anything.");
        var done=new List<string>();
        async Task RemoveAcl(string path,bool token){await Request(HttpMethod.Put,"access/acl",new(){["path"]=path,[token?"tokens":"users"]=token?current.TokenId:current.User,["roles"]=current.Role,["delete"]="1"});done.Add("access assignment "+path);}
        try {
            foreach(var path in current.TokenPaths)await RemoveAcl(path,true);
            if(current.TokenExists){await Request(HttpMethod.Delete,"access/users/"+Uri.EscapeDataString(current.User)+"/token/"+current.TokenId.Split('!')[1]);done.Add("token");}
            if(current.RemoveAccount) {
                // Re-audit before deleting shared-capable objects, including after a partial retry.
                var fresh=await InspectCleanupAsync(current.TokenId);
                if(!fresh.RemoveAccount)throw new InvalidOperationException("The account or role is now shared; it has been retained.");
                foreach(var path in fresh.UserPaths)await RemoveAcl(path,false);
                if(fresh.UserExists){await Request(HttpMethod.Delete,"access/users/"+Uri.EscapeDataString(current.User));done.Add("account");}
                fresh=await InspectCleanupAsync(current.TokenId);
                if(!fresh.RemoveAccount)throw new InvalidOperationException("The role is now shared; it has been retained.");
                if(fresh.RoleExists){await Request(HttpMethod.Delete,"access/roles/"+current.Role);done.Add("role");}
            }
            var after=await InspectCleanupAsync(current.TokenId);
            if(after.TokenExists||after.TokenPaths.Length!=0||current.RemoveAccount&&(after.UserExists||after.RoleExists))throw new InvalidOperationException("Removal could not be verified.");
            return current.RemoveAccount?"DALIZO token, account, role and access assignments removed.":"DALIZO token removed. Shared or modified account and role retained.";
        }catch(Exception){throw new InvalidOperationException("Access removal did not finish or could not be verified. The local profile is retained. Confirmed removals: "+(done.Count==0?"none":string.Join(", ",done))+". A timed-out request may have completed. Inspect again before retrying. Target: "+current.TokenId+"; role "+current.Role+".");}
    }
    public async Task<SetupPlan[]> FindReusableAsync(string[] paths)
    {
        await RequireAccessAudit();
        var users=await Request(HttpMethod.Get,"access/users?full=1");var roles=await Request(HttpMethod.Get,"access/roles");var acl=await Request(HttpMethod.Get,"access/acl");
        var result=new List<SetupPlan>();
        foreach(var u in users.EnumerateArray()) {
            string user=Text(u,"userid");
            if(Text(u,"comment")!="DALIZO dedicated API account"||Text(u,"enable") is not ("1" or "True")||Text(u,"groups") is not ("" or "[]")||!TryManagedAccess(user+"!client",out _,out var role))continue;
            if(!roles.EnumerateArray().Any(r=>Text(r,"roleid")==role&&Text(r,"privs").Split(new[]{' ',','},StringSplitOptions.RemoveEmptyEntries).ToHashSet().SetEquals(SetupPlan.RolePrivileges)))continue;
            var a=acl.EnumerateArray().Where(x=>Text(x,"type")=="user"&&Text(x,"ugid")==user).ToArray();
            if(a.Any(x=>Text(x,"roleid")!=role||Text(x,"propagate") is not ("1" or "True"))||!a.Select(x=>Text(x,"path")).ToHashSet().SetEquals(paths))continue;
            result.Add(new(role,user,"client-"+Guid.NewGuid().ToString("N")[..12],paths));
        }
        return result.ToArray();
    }
    public async Task<SetupResult> ProvisionReusedAsync(SetupPlan plan,Func<SetupResult,Task> verify)
    {
        if(!Regex.IsMatch(plan.Token,@"^client-[a-f0-9]{12}$")||!TryManagedAccess(plan.FullTokenId,out var user,out var role)||user!=plan.User||role!=plan.Role)throw new ArgumentException("Invalid reuse plan.");
        var eligible=await FindReusableAsync(plan.Paths);
        if(!eligible.Any(p=>p.User==plan.User&&p.Role==plan.Role))throw new InvalidOperationException("The account permissions changed; reuse was cancelled.");
        string path="access/users/"+Uri.EscapeDataString(plan.User)+"/token/"+plan.Token;
        var tokens=await Request(HttpMethod.Get,"access/users/"+Uri.EscapeDataString(plan.User)+"/token");
        if(tokens.EnumerateArray().Any(t=>Text(t,"tokenid")==plan.Token))throw new InvalidOperationException("Token name already exists; nothing changed.");
        bool created=false;var granted=new List<string>();
        try {
            var t=await Request(HttpMethod.Post,path,new(){["privsep"]="1",["expire"]="0",["comment"]="DALIZO client"});created=true;
            var result=new SetupResult(t.GetProperty("full-tokenid").GetString()!,t.GetProperty("value").GetString()!);
            if(result.TokenId!=plan.FullTokenId||string.IsNullOrWhiteSpace(result.Secret))throw new InvalidOperationException("Unexpected token response.");
            foreach(var scope in plan.Paths){await Request(HttpMethod.Put,"access/acl",new(){["path"]=scope,["tokens"]=plan.FullTokenId,["roles"]=plan.Role,["propagate"]="1"});granted.Add(scope);}
            await verify(result);return result;
        }catch {
            bool failed=false;
            foreach(var scope in granted)try{await Request(HttpMethod.Put,"access/acl",new(){["path"]=scope,["tokens"]=plan.FullTokenId,["roles"]=plan.Role,["delete"]="1"});}catch{failed=true;}
            if(created)try{await Request(HttpMethod.Delete,path);}catch{failed=true;}
            throw new InvalidOperationException("Token creation failed. Existing account and role were not changed. Inspect token "+plan.FullTokenId+" before retrying; a timed-out request may have completed. "+(failed?"Cleanup requires attention.":"Cleanup of confirmed changes was attempted."));
        }
    }
}


