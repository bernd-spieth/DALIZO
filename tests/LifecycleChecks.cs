// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ProxmoxSpice;
static class LifecycleChecks
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    public static async Task Run()
    {
        foreach(string mode in new[]{"exclusive","legacy-localnetwork","shared-token","shared-role","modified-role","partial","absent"}) {
            var f=new Server(mode);using var api=new ProxmoxSetup("https://example.test",f);await api.LoginAsync("root@pam","test-password");
            var preview=await api.InspectCleanupAsync(f.Plan.FullTokenId);
            bool exclusive=mode is "exclusive" or "legacy-localnetwork" or "partial" or "absent";
            Check(preview.RemoveAccount==exclusive,"Wrong shared-account decision: "+mode);
            await api.CleanupAsync(preview);
            Check(!f.Tokens.Contains("client")&&f.Acls.All(a=>a["ugid"]!=f.Plan.FullTokenId),"Target token/access retained");
            Check(exclusive?!f.User&&!f.Role:f.User&&f.Role,"Wrong account/role removal");
        }
        foreach(string mode in new[]{"limited","unmarked","modified-token-acl","changed-after-review","failed-delete"}) {
            var f=new Server(mode);using var api=new ProxmoxSetup("https://example.test",f);await api.LoginAsync("root@pam","test-password");
            try {
                var p=await api.InspectCleanupAsync(f.Plan.FullTokenId);
                if(mode=="changed-after-review")f.Tokens.Add("another");
                await api.CleanupAsync(p);throw new Exception("Unsafe cleanup accepted: "+mode);
            }catch(InvalidOperationException) {
                if(mode!="failed-delete")Check(f.Writes==0,"Wrote before safe preflight: "+mode);
                else {Check(f.User&&f.Role,"Continued deleting after failure");f.Mode="exclusive";await api.CleanupAsync(await api.InspectCleanupAsync(f.Plan.FullTokenId));Check(!f.User&&!f.Role,"Partial cleanup retry failed");}
            }
        }
        foreach(bool perVm in new[]{false,true}) {
            var f=new Server("exclusive",perVm);using var api=new ProxmoxSetup("https://example.test",f);await api.LoginAsync("root@pam","test-password");
            var found=await api.FindReusableAsync(f.Plan.Paths);Check(found.Length==1&&f.Writes==0,"Reuse discovery changed access or failed");
            Check((await api.FindReusableAsync(new[]{"/vms/999"})).Length==0,"Wrong scope reused");
            var token=await api.ProvisionReusedAsync(found[0],r=>Task.CompletedTask);
            Check(token.TokenId!=f.Plan.FullTokenId&&f.Tokens.Count==2&&f.User&&f.Role,"Reuse modified existing identity");
            await api.CleanupAsync(await api.InspectCleanupAsync(token.TokenId));Check(f.User&&f.Role&&f.Tokens.SetEquals(new[]{"client"}),"Reuse cleanup destroyed original access");
        }
        {
            var f=new Server("exclusive");using var api=new ProxmoxSetup("https://example.test",f);await api.LoginAsync("root@pam","test-password");
            var plan=(await api.FindReusableAsync(f.Plan.Paths)).Single();
            try{await api.ProvisionReusedAsync(plan,_=>Task.FromException(new Exception("test-secret")));throw new Exception("Expected verification failure");}
            catch(InvalidOperationException ex){Check(!ex.Message.Contains("test-secret")&&f.Tokens.SetEquals(new[]{"client"})&&f.User&&f.Role,"Reuse rollback damaged shared access");}
        }
        {
            var f=new Server("legacy-localnetwork");using var api=new ProxmoxSetup("https://example.test",f);await api.LoginAsync("root@pam","test-password");
            Check((await api.FindReusableAsync(f.Plan.Paths)).Length==0 && f.Writes==0,"Legacy network scope was silently reused or widened");
        }
        foreach(bool perVm in new[]{false,true}) {
            var f=new Server("legacy-localnetwork",perVm);f.Privileges.Remove("SDN.Use");
            using var api=new ProxmoxSetup("https://example.test",f);await api.LoginAsync("root@pam","test-password");
            var preview=await api.InspectRepairAsync(f.Plan.FullTokenId);
            Check(preview.Grants.Length==2&&preview.MissingPrivileges.SequenceEqual(new[]{"SDN.Use"})&&f.Writes==0,"Repair preview changed state or missed SDN grants");
            bool verified=false;f.Acls.Reverse(); // API ordering alone is not access drift.
            await api.RepairAccessAsync(preview,paths=>{Check(paths.SequenceEqual(f.Plan.Paths.OrderBy(p=>p==SetupPlan.BridgePath?1:0).ThenBy(p=>p)),"Repair widened VM scope");verified=true;return Task.CompletedTask;});
            Check(verified&&f.Writes==3&&f.Tokens.SetEquals(new[]{"client"}),"Repair did not verify or replaced token");
            int writes=f.Writes;var again=await api.InspectRepairAsync(f.Plan.FullTokenId);Check(!again.HasChanges,"Repair not idempotent");
            await api.RepairAccessAsync(again,_=>Task.CompletedTask);Check(f.Writes==writes,"Repeat repair wrote changes");
        }
        foreach(string mode in new[]{"shared-role","modified-role","modified-token-acl","limited","unmarked"}) {
            var f=new Server(mode);using var api=new ProxmoxSetup("https://example.test",f);await api.LoginAsync("root@pam","test-password");
            if(mode=="modified-role")f.Privileges.Add("Sys.Modify");
            bool refused=false;try{await api.InspectRepairAsync(f.Plan.FullTokenId);}catch(InvalidOperationException){refused=true;}
            Check(refused&&f.Writes==0,"Unsafe repair accepted: "+mode);
        }
        {
            var f=new Server("legacy-localnetwork");using var api=new ProxmoxSetup("https://example.test",f);await api.LoginAsync("root@pam","test-password");
            var preview=await api.InspectRepairAsync(f.Plan.FullTokenId);f.Tokens.Add("another");
            bool refused=false;try{await api.RepairAccessAsync(preview,_=>Task.CompletedTask);}catch(InvalidOperationException){refused=true;}
            Check(refused&&f.Writes==0,"Repair ignored preflight drift");
            preview=await api.InspectRepairAsync(f.Plan.FullTokenId);Check(preview.Shared,"Shared-account warning missing");
            bool failed=false;try{await api.RepairAccessAsync(preview,_=>Task.FromException(new Exception("synthetic-secret")));}catch(InvalidOperationException ex){failed=!ex.Message.Contains("synthetic-secret")&&ex.Message.Contains("retained");}
            Check(failed&&f.Tokens.Contains("client"),"Verification failure was hidden or token changed");
            await api.RepairAccessAsync(await api.InspectRepairAsync(f.Plan.FullTokenId),_=>Task.CompletedTask);
        }
        Console.WriteLine("PASS: permission repair preview, all-VM/per-VM scope preservation, SDN role append, user/token grants, idempotence, custom/shared-role refusal, drift and verification-failure retry. Synthetic HTTP only.");
        Check(!ProxmoxSetup.TryManagedAccess("root@pam!client",out _,out _),"Manual admin identity accepted");
        Console.WriteLine("PASS: cleanup exclusive/shared/modified/missing objects, complete-audit requirement, preflight drift, partial failure retry, all-VM/per-VM reuse, new token isolation and rollback. Synthetic HTTP only; no live deletions.");
    }
    sealed class Server:HttpMessageHandler
    {
        public HashSet<string> Privileges=new(SetupPlan.RolePrivileges);
        public SetupPlan Plan;public bool User=true,Role=true;public string Mode;public int Writes;
        public HashSet<string> Tokens=new(){"client"};public List<Dictionary<string,string>> Acls=new();
        public Server(string mode,bool perVm=false){Mode=mode;Plan=SetupPlan.Create(!perVm,"101,102");foreach(var path in Plan.Paths){Acl(path,Plan.User,"user");Acl(path,Plan.FullTokenId,"token");}
            if(mode=="legacy-localnetwork")foreach(var acl in Acls.Where(a=>a["path"]==SetupPlan.BridgePath))acl["path"]=SetupPlan.LegacyBridgePath; if(mode=="shared-token")Tokens.Add("another");if(mode=="shared-role")Acl("/vms","someone@pve","user");
            if(mode=="modified-token-acl")Acls.Last()["roleid"]="Administrator";
            if(mode=="partial"){Tokens.Clear();Acls.RemoveAll(a=>a["type"]=="token");}
            if(mode=="absent"){User=Role=false;Tokens.Clear();Acls.Clear();}
        }
        void Acl(string path,string subject,string type)=>Acls.Add(new(){["path"]=path,["ugid"]=subject,["type"]=type,["roleid"]=Plan.Role,["propagate"]="1"});
        HttpResponseMessage Reply(object? data)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{data}))};
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
        {
            string path=Uri.UnescapeDataString(req.RequestUri!.AbsolutePath[11..]);
            if(path=="access/ticket")return Reply(new{ticket="PVE:test",CSRFPreventionToken="csrf"});
            Check(req.Headers.Contains("CSRFPreventionToken"),"Missing administrator auth");
            if(req.Method==HttpMethod.Get) {
                if(path=="access/permissions")return Reply(new Dictionary<string,object>{{"/",Mode=="limited"?new Dictionary<string,int>():new Dictionary<string,int>{{"Sys.Audit",1},{"Sys.Modify",1},{"User.Modify",1},{"Permissions.Modify",1}}}});
                if(path=="access/users")return Reply(User?new[]{new{userid=Plan.User,comment=Mode=="unmarked"?"external":"DALIZO dedicated API account",enable=1,groups=""}}:Array.Empty<object>());
                if(path=="access/roles")return Reply(Role?new[]{new{roleid=Plan.Role,privs=Mode=="modified-role"?"Sys.Modify":string.Join(",",Privileges)}}:Array.Empty<object>());
                if(path=="access/acl")return Reply(Acls);
                if(path.EndsWith("/token"))return Reply(Tokens.Select(t=>new{tokenid=t,comment="DALIZO client",privsep=1}));
                throw new Exception("Unexpected read "+path);
            }
            Writes++;
            var body=req.Content==null?"":await req.Content.ReadAsStringAsync(ct);
            var fields=body.Split('&',StringSplitOptions.RemoveEmptyEntries).Select(p=>p.Split('=',2)).ToDictionary(p=>Uri.UnescapeDataString(p[0]),p=>Uri.UnescapeDataString(p[1].Replace('+',' ')));
            if(path.StartsWith("access/roles/")&&req.Method==HttpMethod.Put){Check(fields["append"]=="1","Repair replaced role");Privileges.UnionWith(fields["privs"].Split(' '));return Reply(null);}
            if(path=="access/acl") {
                string type=fields.ContainsKey("tokens")?"token":"user",subject=fields[type=="token"?"tokens":"users"];
                if(fields.TryGetValue("delete",out var d)&&d=="1")Acls.RemoveAll(a=>a["ugid"]==subject&&a["path"]==fields["path"]&&a["roleid"]==fields["roles"]);
                else Acl(fields["path"],subject,type);return Reply(null);
            }
            if(path.Contains("/token/")) {
                string token=path.Split('/').Last();
                if(req.Method==HttpMethod.Post){Check(fields["privsep"]=="1","Unrestricted new token");Tokens.Add(token);return Reply(new Dictionary<string,string>{{"full-tokenid",Plan.User+"!"+token},{"value","test-secret"}});}
                if(Mode=="failed-delete")return new(HttpStatusCode.Forbidden);
                Tokens.Remove(token);return Reply(null);
            }
            if(path.StartsWith("access/users/")&&req.Method==HttpMethod.Delete){User=false;return Reply(null);}
            if(path.StartsWith("access/roles/")&&req.Method==HttpMethod.Delete){Role=false;return Reply(null);}
            throw new Exception("Unexpected mutation "+path);
        }
    }
}

