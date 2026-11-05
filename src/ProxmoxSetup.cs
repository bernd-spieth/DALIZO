// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProxmoxSpice;

public sealed record SetupPlan(string Role, string User, string Token, string[] Paths)
{
    public const string BridgePath="/sdn/zones";
    public const string LegacyBridgePath="/sdn/zones/localnetwork";
    public static string[] RolePrivileges=>ProxmoxApi.RequiredVmPermissions.Select(p=>p.Privilege).Append("SDN.Use").ToArray();
    public static SetupPlan Create(bool allVms, string vmIds)
    {
        var suffix=Guid.NewGuid().ToString("N")[..12];
        var paths=allVms ? new[]{"/vms"} : vmIds.Split(new[]{',',';',' ','\r','\n'},StringSplitOptions.RemoveEmptyEntries)
            .Select(id=>int.TryParse(id,out var value)&&value>0 ? "/vms/"+value : throw new ArgumentException("Enter positive VM IDs separated by commas.")).Distinct().ToArray();
        if(paths.Length==0)throw new ArgumentException("Select all VMs or enter at least one VM ID.");
        return new("DALIZO_"+suffix,"dalizo-"+suffix+"@pve","client",paths.Append(BridgePath).ToArray());
    }
    public void Validate()
    {
        if(!Regex.IsMatch(Role,@"^DALIZO_[a-f0-9]{12}$") || !Regex.IsMatch(User,@"^dalizo-[a-f0-9]{12}@pve$") || Token!="client"
            || Paths.Length==0 || !Paths.Contains(BridgePath) || !Paths.Any(p=>p=="/vms"||p.StartsWith("/vms/")) || Paths.Any(p=>p!=BridgePath&&p!="/vms"&&!Regex.IsMatch(p,@"^/vms/[1-9][0-9]*$")))
            throw new ArgumentException("Invalid setup plan.");
    }
    public string FullTokenId=>User+"!"+Token;
}
// A class deliberately avoids a generated ToString containing the token secret.
public sealed class SetupResult(string id,string secret)
{
    public string TokenId {get;}=id;
    public string Secret {get;}=secret;
}

public sealed partial class ProxmoxSetup : IDisposable
{
    readonly HttpClient http;
    public ProxmoxSetup(string address,HttpMessageHandler? handler=null)
    {
        using var validation=new ProxmoxApi(address);
        http=new HttpClient(handler ?? new HttpClientHandler {AllowAutoRedirect=false,UseCookies=false})
            {BaseAddress=new Uri(address),Timeout=TimeSpan.FromSeconds(25)};
    }
    async Task<JsonElement> Request(HttpMethod method,string path,Dictionary<string,string>? fields=null)
    {
        using var request=new HttpRequestMessage(method,"api2/json/"+path);
        if(fields!=null)request.Content=new FormUrlEncodedContent(fields);
        using var response=await http.SendAsync(request);
        // Never include response bodies, passwords, cookies or token secrets in errors.
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"Proxmox rejected {method} {path} (HTTP {(int)response.StatusCode}).");
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }
    public async Task LoginAsync(string username,string password,Func<Task<string?>>? getTotp=null)
    {
        if(!username.Contains('@') || username.Any(char.IsControl) || string.IsNullOrEmpty(password))
            throw new ArgumentException("Enter your administrator username, including @pam or @pve, and your password.");
        var login=await Request(HttpMethod.Post,"access/ticket",new(){["username"]=username,["password"]=password});
        password="";
        bool NeedsFactor(JsonElement value)=>value.TryGetProperty("NeedTFA",out var needed)&&needed.ToString() is "1" or "True"
            || (value.TryGetProperty("ticket",out var ticket)&&ticket.GetString()?.Contains("!tfa!")==true);
        if(NeedsFactor(login)) {
            var code=getTotp==null ? null : await getTotp();
            if(code==null || !Regex.IsMatch(code,@"^[0-9]{6,8}$"))throw new InvalidOperationException("Enter a valid code from your authenticator app. If you sign in another way, use Manual setup with an existing API token.");
            login=await Request(HttpMethod.Post,"access/ticket",new(){["username"]=username,["password"]="totp:"+code,["tfa-challenge"]=login.GetProperty("ticket").GetString()!});
            if(NeedsFactor(login))throw new InvalidOperationException("The extra sign-in check did not complete. No account or login key was created.");
        }
        var cookie=login.GetProperty("ticket").GetString()!;
        var csrf=login.GetProperty("CSRFPreventionToken").GetString()!;
        if(string.IsNullOrWhiteSpace(cookie)||string.IsNullOrWhiteSpace(csrf)||cookie.Any(char.IsControl)||csrf.Any(char.IsControl))throw new InvalidOperationException("Invalid administrator login response.");
        http.DefaultRequestHeaders.Add("Cookie","PVEAuthCookie="+Uri.EscapeDataString(cookie));
        http.DefaultRequestHeaders.Add("CSRFPreventionToken",csrf);
    }
    public async Task<SetupResult> ProvisionAsync(SetupPlan plan,Func<SetupResult,Task> verify)
    {
        plan.Validate();
        if(!http.DefaultRequestHeaders.Contains("CSRFPreventionToken"))throw new InvalidOperationException("Administrator sign-in is required.");
        var roles=await Request(HttpMethod.Get,"access/roles");
        var users=await Request(HttpMethod.Get,"access/users");
        if(roles.EnumerateArray().Any(r=>r.GetProperty("roleid").GetString()==plan.Role)
            ||users.EnumerateArray().Any(u=>u.GetProperty("userid").GetString()==plan.User))
            throw new InvalidOperationException("The generated role or user already exists. Nothing was changed. Reopen setup to generate new names.");
        // Validate selected targets before creating access. Templates are not connectable.
        if(!plan.Paths.Contains("/vms")) {
            var inventory=await Request(HttpMethod.Get,"cluster/resources?type=vm");
            var available=inventory.EnumerateArray().Where(v=>v.GetProperty("type").GetString()=="qemu"
                && (!v.TryGetProperty("template",out var template)||template.ToString() is "0" or "False"))
                .Select(v=>"/vms/"+v.GetProperty("vmid").GetInt32()).ToHashSet();
            if(plan.Paths.Where(p=>p!=SetupPlan.BridgePath).Any(p=>!available.Contains(p)))throw new InvalidOperationException("A selected VM is missing, invisible or a template. Nothing was changed.");
        }
        var rollback=new Stack<(HttpMethod Method,string Path,Dictionary<string,string>? Fields)>();
        string stage="Create role";
        var userPath="access/users/"+Uri.EscapeDataString(plan.User);
        var tokenPath=userPath+"/token/"+plan.Token;
        Dictionary<string,string> Acl(string path,string subject,bool token,bool remove=false)=>new() {
            ["path"]=path,[token?"tokens":"users"]=subject,["roles"]=plan.Role,["propagate"]="1",["delete"]=remove?"1":"0"
        };
        try {
            await Request(HttpMethod.Post,"access/roles",new(){["roleid"]=plan.Role,["privs"]=string.Join(" ",SetupPlan.RolePrivileges)});
            rollback.Push((HttpMethod.Delete,"access/roles/"+plan.Role,null));
            stage="Create dedicated user";
            await Request(HttpMethod.Post,"access/users",new(){["userid"]=plan.User,["enable"]="1",["comment"]="DALIZO dedicated API account"});
            rollback.Push((HttpMethod.Delete,userPath,null));
            foreach(var path in plan.Paths) {
                stage="Grant user access at "+path;
                await Request(HttpMethod.Put,"access/acl",Acl(path,plan.User,false));
                rollback.Push((HttpMethod.Put,"access/acl",Acl(path,plan.User,false,true)));
            }
            stage="Create restricted token";
            var result=await Request(HttpMethod.Post,tokenPath,new(){["privsep"]="1",["expire"]="0",["comment"]="DALIZO client"});
            rollback.Push((HttpMethod.Delete,tokenPath,null));
            var token=new SetupResult(result.GetProperty("full-tokenid").GetString()!,result.GetProperty("value").GetString()!);
            if(token.TokenId!=plan.FullTokenId || string.IsNullOrWhiteSpace(token.Secret))throw new InvalidOperationException("Unexpected token response.");
            foreach(var path in plan.Paths) {
                stage="Grant token access at "+path;
                await Request(HttpMethod.Put,"access/acl",Acl(path,plan.FullTokenId,true));
                rollback.Push((HttpMethod.Put,"access/acl",Acl(path,plan.FullTokenId,true,true)));
            }
            stage="Verify generated token permissions";
            await verify(token);
            return token;
        } catch {
            var failed=new List<string>();
            // Only reverse confirmed operations on the unique objects created here.
            while(rollback.TryPop(out var undo)) {
                try {await Request(undo.Method,undo.Path,undo.Fields);} catch {failed.Add(undo.Path);}
            }
            throw new InvalidOperationException($"Setup failed during: {stage}. Cleanup of confirmed changes was attempted. "
                +"A timed-out request may still have completed; inspect these objects before retrying: "
                +$"role {plan.Role}, user {plan.User}, token {plan.FullTokenId}. "
                +(failed.Count==0?"No cleanup request failed.":"Cleanup requires attention: "+string.Join(", ",failed)));
        }
    }
    public void Dispose()=>http.Dispose();
}


