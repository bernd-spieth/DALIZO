// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ProxmoxSpice;

static class SetupChecks
{
    static void Check(bool condition,string message) {if(!condition)throw new Exception(message);}
    public static async Task Run()
    {
        foreach(bool perVm in new[]{false,true}) {
            var plan=SetupPlan.Create(!perVm,"101,102,101");
            var fake=new SetupServer(plan){TwoFactor=perVm};
            using var client=new ProxmoxSetup("https://example.test",fake);
            await client.LoginAsync("root@pam","test-password",()=>Task.FromResult<string?>("123456"));
            bool verified=false;
            var result=await client.ProvisionAsync(plan,t=> {
                Check(t.TokenId==plan.FullTokenId&&t.Secret=="test-secret","Wrong generated token");verified=true;return Task.CompletedTask;
            });
            Check(verified&&fake.Role&&fake.User&&fake.Token,"Setup did not complete");
            Check(plan.Paths.Contains(SetupPlan.BridgePath)&&fake.Acls.Count(a=>a["path"]==SetupPlan.BridgePath)==2,"Bridge ACL missing for user or token");
            Check(fake.Acls.Count==plan.Paths.Length*2,"User/token ACLs missing");
            Check(fake.Acls.All(a=>plan.Paths.Contains(a["path"])&&a["propagate"]=="1"),"Wrong ACL scope");
            Check(!perVm || !fake.Acls.Any(a=>a["path"]=="/vms"),"Per-VM scope widened");
            Check(!result.ToString()!.Contains("test-secret"),"Secret exposed by ToString");
        }
        foreach(var mode in new[]{"conflict","token-acl-denied","verification-failed","auth-denied","unknown-write","cleanup-failed"}) {
            var plan=SetupPlan.Create(true,"");
            var fake=new SetupServer(plan){Mode=mode};
            using var client=new ProxmoxSetup("https://example.test",fake);
            try {
                await client.LoginAsync("root@pam","test-password");
                await client.ProvisionAsync(plan,_=>mode=="verification-failed"?Task.FromException(new Exception("test-secret")):Task.CompletedTask);
                throw new Exception("Unexpected setup success: "+mode);
            } catch(InvalidOperationException e) {
                Check(!e.Message.Contains("test-secret")&&!e.Message.Contains("test-password"),"Secret leaked in setup error");
                if(mode is "conflict" or "auth-denied")Check(fake.Mutations==0,"Changed existing access or wrote before authentication");
                else if(mode is "token-acl-denied" or "verification-failed")Check(!fake.Role&&!fake.User&&!fake.Token&&fake.Acls.Count==0,"Confirmed objects not cleaned up");
                else if(mode=="unknown-write")Check(e.Message.Contains(plan.Role)&&e.Message.Contains("may still have completed"),"Ambiguous result not reported");
                else Check(e.Message.Contains("Cleanup requires attention"),"Failed cleanup not reported");
            }
        }
        var mfaPlan=SetupPlan.Create(true,"");
        var mfa=new SetupServer(mfaPlan){TwoFactor=true};
        using(var client=new ProxmoxSetup("https://example.test",mfa)) {
            try {await client.LoginAsync("root@pam","test-password");throw new Exception("Incomplete MFA accepted");}
            catch(InvalidOperationException){Check(mfa.Mutations==0,"Wrote with partial MFA ticket");}
        }
        foreach(bool missing in new[]{false,true}) {
            using var verify=new ProxmoxApi("https://example.test",new Fake(req=> {
                var rights=ProxmoxApi.RequiredVmPermissions.Where(p=>!missing||p.Privilege!="VM.Console").ToDictionary(p=>p.Privilege,p=>1);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new {data=(object)(req.RequestUri!.AbsolutePath.EndsWith("permissions")?new Dictionary<string,object>{{"/vms",rights}}:Array.Empty<object>())}))});
            }));
            verify.UseToken("test@pve!client","test-secret");
            try {await verify.VerifySetupPermissionsAsync(new[]{"/vms"});Check(!missing,"Missing rights accepted");}
            catch(InvalidOperationException){Check(missing,"Valid rights rejected");}
        }
        foreach(int allowed in new[]{1,0,-1}) {
            using var verify=new ProxmoxApi("https://example.test",new Fake(req=>{
                var rights=allowed<0?new Dictionary<string,int>():new Dictionary<string,int>{{"SDN.Use",allowed}};
                object response=req.RequestUri!.AbsolutePath.EndsWith("permissions")?new Dictionary<string,object>{{SetupPlan.BridgePath,rights}}:Array.Empty<object>();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{data=response}))});
            }));verify.UseToken("test@pve!client","synthetic-secret");bool passed=false;
            try{await verify.VerifySetupPermissionsAsync(new[]{SetupPlan.BridgePath});passed=true;}catch(InvalidOperationException){}
            Check(passed==(allowed==1),"Inherited bridge SDN.Use verification failed");
        }
        Console.WriteLine("PASS: automatic setup login/CSRF, TOTP, exact role rights, privilege-separated token, all-VM/per-VM ACLs, collision refusal, cleanup, ambiguous writes, secret-safe errors and token permission verification. Synthetic HTTP only.");
    }
    sealed class SetupServer(SetupPlan plan):HttpMessageHandler
    {
        public bool TwoFactor,Role,User,Token;
        public string Mode="";
        public int Mutations;
        public List<Dictionary<string,string>> Acls=new();
        HttpResponseMessage Reply(object? data)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{data}))};
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=Uri.UnescapeDataString(request.RequestUri!.AbsolutePath[11..]); // /api2/json/
            var body=request.Content==null?"":await request.Content.ReadAsStringAsync(ct);
            var fields=body.Split('&',StringSplitOptions.RemoveEmptyEntries).Select(p=>p.Split('=',2))
                .ToDictionary(p=>Uri.UnescapeDataString(p[0]),p=>Uri.UnescapeDataString(p[1].Replace('+',' ')));
            if(path=="access/ticket") {
                if(Mode=="auth-denied")return new(HttpStatusCode.Unauthorized);
                if(TwoFactor&&!fields.ContainsKey("tfa-challenge"))return Reply(new{ticket="PVE:!tfa!challenge",NeedTFA=1,CSRFPreventionToken="partial"});
                if(TwoFactor)Check(fields["password"]=="totp:123456"&&fields["tfa-challenge"]=="PVE:!tfa!challenge","Incorrect TOTP exchange");
                return Reply(new{ticket="PVE:test-ticket",CSRFPreventionToken="test-csrf"});
            }
            Check(request.Headers.Contains("Cookie")&&request.Headers.GetValues("CSRFPreventionToken").Single()=="test-csrf","Missing ticket/CSRF or incomplete MFA used");
            if(request.Method==HttpMethod.Get) {
                if(path=="access/roles")return Reply(Mode=="conflict"?new[]{new{roleid=plan.Role}}:Array.Empty<object>());
                if(path=="access/users")return Reply(Array.Empty<object>());
                if(path=="cluster/resources")return Reply(new[]{new{type="qemu",vmid=101,template=0},new{type="qemu",vmid=102,template=0}});
                throw new Exception("Unexpected read "+path);
            }
            Mutations++;
            if(path=="access/roles"&&request.Method==HttpMethod.Post) {
                Check(fields["privs"].Split(' ').ToHashSet().SetEquals(SetupPlan.RolePrivileges),"Unexpected role privileges");
                Role=true;if(Mode=="unknown-write")throw new HttpRequestException("Synthetic lost response");
            }
            else if(path=="access/users"&&request.Method==HttpMethod.Post) {Check(!fields.ContainsKey("password"),"Service account password unexpectedly set");User=true;}
            else if(path=="access/acl") {
                if(fields["delete"]=="1")Acls.RemoveAll(a=>a["path"]==fields["path"]&&a.GetValueOrDefault("users")==fields.GetValueOrDefault("users")&&a.GetValueOrDefault("tokens")==fields.GetValueOrDefault("tokens"));
                else {if(fields.ContainsKey("tokens")&&Mode is "token-acl-denied" or "cleanup-failed")return new(HttpStatusCode.Forbidden);Acls.Add(fields);}
            }
            else if(path.EndsWith("/token/client")&&request.Method==HttpMethod.Post) {
                Check(fields["privsep"]=="1","Token privilege separation disabled");Token=true;
                return Reply(new Dictionary<string,string>{{"full-tokenid",plan.FullTokenId},{"value","test-secret"}});
            }
            else if(request.Method==HttpMethod.Delete) {
                if(Mode=="cleanup-failed")return new(HttpStatusCode.Forbidden);
                if(path.EndsWith("/token/client"))Token=false;
                else if(path.StartsWith("access/users/"))User=false;
                else if(path.StartsWith("access/roles/"))Role=false;
            }
            else throw new Exception("Unexpected write "+path);
            return Reply(null);
        }
    }
}
