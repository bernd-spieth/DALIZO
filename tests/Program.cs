// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using ProxmoxSpice;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
var calls = new List<(string Path, string Body, string Auth)>();
using var api = new ProxmoxApi("https://pve.example.test:8006", new Fake(async req =>
{
    var path = req.RequestUri!.PathAndQuery;
    var body = req.Content == null ? "" : await req.Content.ReadAsStringAsync();
    calls.Add((path, body, req.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : ""));
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(path.Contains("resources")
        ? "{\"data\":[{\"type\":\"qemu\",\"vmid\":101,\"node\":\"pve1\",\"name\":\"Test\",\"status\":\"running\"},{\"type\":\"lxc\",\"vmid\":102}]}"
        : "[virt-viewer]\ntype=spice\npassword=synthetic-test-ticket\n")
    };
}));
api.UseToken("test@pve!console", "synthetic-secret");
var vms = await api.ListVmsAsync();
Check(vms.Count == 1 && vms[0].Id == 101, "QEMU filtering failed");
await api.GetConsoleAsync(vms[0], "pve.example.test");
await api.GetConsoleAsync(vms[0], "pve.example.test");
Check(calls.Count == 3, "Each console request must obtain a fresh ticket");
Check(calls[1].Path == "/api2/spiceconfig/nodes/pve1/qemu/101/spiceproxy", "Wrong endpoint");
Check(calls[1].Body == "proxy=pve.example.test", "Wrong proxy body");
Check(calls[1].Auth == "PVEAPIToken=test@pve!console=synthetic-secret", "Wrong token header");
try { using var invalid = new ProxmoxApi("http://pve.example.test"); throw new Exception("HTTP accepted"); }
catch (ArgumentException) { }
using var forbidden = new ProxmoxApi("https://pve.example.test", new Fake(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
try { await forbidden.ListVmsAsync(); throw new Exception("403 accepted"); }
catch (HttpRequestException e) { Check(!e.Message.Contains("synthetic-secret"), "Secret leaked"); }
var validConfig = "[virt-viewer]\ntype=spice\nhost=pvespiceproxy:synthetic\ntls-port=61000\npassword=synthetic-ticket\nproxy=http://pve.example.test:3128\nhost-subject=CN=pve\nca=-----BEGIN CERTIFICATE-----\\nTEST\\n-----END CERTIFICATE-----\n";
var config = SpiceConfig.Parse(validConfig);
Check(config.Ca.Contains("\nTEST\n") && config.Password == "synthetic-ticket", "CA escaping or ticket parsing failed");
foreach (var malformed in new[] { validConfig.Replace("type=spice", "type=vnc"), validConfig.Replace("tls-port=61000", "tls-port=0"), validConfig.Replace("http://pve.example.test:3128", "file:///tmp/test"), validConfig + "password=duplicate\n", validConfig.Replace("host-subject=CN=pve\n", ""), validConfig.Replace("-----BEGIN CERTIFICATE-----", "invalid") })
{
    try { SpiceConfig.Parse(malformed); throw new Exception("Malformed configuration accepted"); } catch (FormatException) { }
}
try { api.UseToken("test@pve!console", "secret\r\nInjected: value"); throw new Exception("Header injection accepted"); } catch (ArgumentException) { }
Console.WriteLine("PASS: CA newline decoding; rejection of wrong protocol, invalid port, proxy scheme, duplicate key, missing subject, missing CA, header injection.");
Console.WriteLine("PASS: VM filtering, fresh requests, endpoint, proxy, token header, HTTPS requirement, forbidden response.");

var folder = Path.Combine(Directory.GetCurrentDirectory(), "work", "ProxmoxSpice-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var store = new ProfileStore(Path.Combine(folder, "profiles.json"));
    Check(store.Load().Servers.Count == 0, "New store not empty");
    var encrypted = SecretProtection.Protect("synthetic-secret");
    Check(SecretProtection.Unprotect(encrypted) == "synthetic-secret", "DPAPI roundtrip failed");
    var server = new ServerProfile(Guid.NewGuid(), "Lab", "https://pve.example.test:8006", "test@pve!console", "", encrypted);
    var favorite = new SavedVm(Guid.NewGuid(), server.Id, 101, "My VM");
    var saved = new WorkspaceData(1, new() { server }, new() { favorite });
    store.Save(saved);
    Check(store.Load().Vms.Single() == favorite, "Saved VM did not survive reload");
    var settingsVm=favorite with {ClipboardEnabled=false,AutoResolution=false};
    store.Save(saved with {Vms=new(){settingsVm}});
    Check(store.Load().Vms.Single() is {ClipboardEnabled:false,AutoResolution:false},"Per-VM settings were not persisted");
    var legacy=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.FilePath))!;
    var legacySettingsVm=legacy["Vms"]![0]!.AsObject();legacySettingsVm.Remove("ClipboardEnabled");legacySettingsVm.Remove("AutoResolution");
    File.WriteAllText(store.FilePath,legacy.ToJsonString());
    Check(store.Load().Vms.Single() is {ClipboardEnabled:true,AutoResolution:true},"Legacy settings did not retain enabled defaults");
    store.Save(saved);
    Console.WriteLine("PASS: per-VM clipboard/auto-resolution settings persist; old profiles default to enabled.");
    Check(!File.ReadAllText(store.FilePath).Contains("synthetic-secret"), "Plaintext secret persisted");
    foreach(var migration in new[]{("SPICE",false,"Auto"),("noVNC",false,"noVNC"),("SPICE",true,"SPICE"),("Auto",true,"Auto")}) {
        store.Save(saved with {Vms=new(){favorite with {ConsoleType=migration.Item1,ExplicitConsole=migration.Item2}}});
        var beforeMigration=File.ReadAllText(store.FilePath);
        Check(store.Load().Vms.Single().ConsoleType==migration.Item3,"Legacy console choice migration failed");
        Check(File.ReadAllText(store.FilePath)==beforeMigration,"Profile load silently changed disk contents");
    }
    store.Save(saved);
    var renamed = favorite with { Name = "Renamed VM" }; saved.Vms[0] = renamed; store.Save(saved);
    Check(store.Load().Vms[0].Name == "Renamed VM", "Rename not persisted");
    var good = File.ReadAllText(store.FilePath);
    foreach (var invalid in new[] { new WorkspaceData(2, saved.Servers, saved.Vms), new WorkspaceData(1, new(), saved.Vms), new WorkspaceData(1, saved.Servers, new() { renamed, renamed with { Id = Guid.NewGuid() } }), new WorkspaceData(1, saved.Servers, new() { renamed with { Name = " " } }) })
    {
        try { store.Save(invalid); throw new Exception("Invalid profile accepted"); } catch (InvalidDataException) { }
        Check(File.ReadAllText(store.FilePath) == good, "Invalid save damaged prior data");
    }
    saved.Vms.Clear(); store.Save(saved); Check(store.Load().Vms.Count == 0, "Delete not persisted");
    File.WriteAllText(store.FilePath, "broken json");
    try { store.Load(); throw new Exception("Corrupt data accepted"); } catch (System.Text.Json.JsonException) { }
    Check(File.ReadAllText(store.FilePath) == "broken json", "Corrupt data silently overwritten");
    Console.WriteLine("PASS: profile create/reload/rename/delete; duplicate, orphan and version rejection; invalid-write preservation; corrupt-file preservation; DPAPI roundtrip and no plaintext secret in JSON.");
}
finally { foreach (var file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
var managementCalls = new List<(string Method, string Path, string Body)>();
using var management = new ProxmoxApi("https://pve.example.test:8006", new Fake(async req =>
{
    var path = req.RequestUri!.AbsolutePath; var body = req.Content == null ? "" : await req.Content.ReadAsStringAsync();
    managementCalls.Add((req.Method.Method, path, body));
    var response = path.EndsWith("status/current") ? "{\"data\":{\"status\":\"running\",\"qmpstatus\":\"paused\",\"uptime\":42}}" :
        path.Contains("/tasks/") ? "{\"data\":{\"status\":\"stopped\",\"exitstatus\":\"ERROR: test failure\"}}" :
        "{\"data\":\"UPID:task-node:123:456:789:qmstart:101:test@pve!console:\"}";
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
}));
var testVm = new Vm(101, "current-node", "Test", "running");
Check((await management.GetRuntimeAsync(testVm)).Label == "Paused", "Paused VM misreported");
foreach (var command in new[] { "start", "shutdown", "reboot", "stop" })
{
    var job = await management.PowerAsync(testVm, command);
    Check(job!.Node == "task-node", "Task host was not extracted from UPID");
}
Check(managementCalls.Single(c => c.Path.EndsWith("/shutdown")).Body == "forceStop=0", "Shutdown can escalate to stop");
Check(managementCalls.Count(c => c.Method == "POST") == 4, "Power commands retried or missing");
try { await management.PowerAsync(testVm, "reset"); throw new Exception("Unexpected action allowed"); } catch (ArgumentException) { }
var baseline = new VmConfiguration(new string('a', 40), new() { ["name"] = "test-vm", ["description"] = "old", ["cores"] = "2", ["sockets"] = "1", ["memory"] = "current=2048,max=8192", ["balloon"] = "1024" });
var delta = baseline.Changes("test-vm", "new", 2, 1, 4096);
Check(delta.Count == 2 && delta["memory"] == "current=4096,max=8192" && !delta.ContainsKey("cores"), "Unchanged settings overwritten or RAM attributes lost");
await management.UpdateConfigurationAsync(testVm, baseline.Digest, delta);
Check(managementCalls.Last().Body.Contains("digest=" + new string('a', 40)), "Config digest omitted");
try { baseline.Changes("test-vm", "new", 2, 1, 512); throw new Exception("Balloon minimum ignored"); } catch (ArgumentException) { }
try { await management.UpdateConfigurationAsync(testVm, "", delta); throw new Exception("Missing digest accepted"); } catch (ArgumentException) { }
try { await management.UpdateConfigurationAsync(testVm, baseline.Digest, new Dictionary<string, string> { { "delete", "scsi0" } }); throw new Exception("Unrelated property accepted"); } catch (ArgumentException) { }
var result = await management.GetJobAsync(new("task-node", "UPID:task-node:test:"));
Check(result.ExitStatus != "OK" && result.Status == "stopped", "Failed task treated as success");
Console.WriteLine("PASS: current/paused state, all four power endpoints, no forced shutdown fallback, task node and failure status, changed-only config, RAM attribute preservation, balloon bound, digest and configuration allowlist.");
var vncCalls=new List<(string Path,string Body,string Auth)>();
int vncRequests=0;
using var vncApi=new ProxmoxApi("https://pve.example.test:8006",new Fake(async req=>{
    vncCalls.Add((req.RequestUri!.PathAndQuery,await req.Content!.ReadAsStringAsync(),req.Headers.GetValues("Authorization").Single()));
    vncRequests++;
    var response=vncRequests==1 ? "{\"data\":{\"user\":\"test@pve\",\"ticket\":\"ticket:+/=&\",\"port\":5900}}" : "{\"data\":{\"user\":\"test@pve\",\"ticket\":\"new-ticket\",\"password\":\"explicit-password\",\"port\":\"5901\"}}";
    return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(response)};
}));
vncApi.UseToken("test@pve!console","synthetic-secret");
var oldVnc=await vncApi.GetVncTicketAsync(testVm);var newVnc=await vncApi.GetVncTicketAsync(testVm);
Check(oldVnc.Password==oldVnc.Ticket && newVnc.Password=="explicit-password","Old/new VNC password compatibility failed");
Check(vncCalls.Count==2 && vncCalls.All(c=>c.Path=="/api2/json/nodes/current-node/qemu/101/vncproxy" && c.Body=="websocket=1" && c.Auth=="PVEAPIToken=test@pve!console=synthetic-secret"),"VNC fresh ticket/endpoint/auth mismatch");
var ws=vncApi.VncWebSocketUri(testVm,oldVnc);
Check(ws.Scheme=="wss" && ws.Host=="pve.example.test" && ws.Port==8006 && ws.Query=="?port=5900&vncticket=ticket%3A%2B%2F%3D%26","VNC WSS endpoint or ticket escaping failed");
foreach(var port in new[]{0,5899,6000,65535}) {
    using var bad=new ProxmoxApi("https://pve.example.test",new Fake(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent($"{{\"data\":{{\"user\":\"test\",\"ticket\":\"t\",\"port\":{port}}}}}")})));
    try {await bad.GetVncTicketAsync(testVm);throw new Exception("Invalid VNC port accepted");}catch(InvalidOperationException){}
}
var legacyVm=System.Text.Json.JsonSerializer.Deserialize<SavedVm>("{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"ServerId\":\"00000000-0000-0000-0000-000000000002\",\"VmId\":101,\"Name\":\"Legacy\"}");
Check(legacyVm!.ConsoleType=="Auto","Existing VM did not receive Auto default");
var noVncVm=legacyVm with {ConsoleType="noVNC"};
Check(System.Text.Json.JsonSerializer.Deserialize<SavedVm>(System.Text.Json.JsonSerializer.Serialize(noVncVm))==noVncVm,"noVNC selection did not roundtrip");
Console.WriteLine("PASS: noVNC fresh tickets, token header, legacy/explicit password, WSS URL escaping, invalid ports, old-profile Auto default and saved noVNC selection.");
using(var inventoryApi=new ProxmoxApi("https://pve.example.test",new Fake(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"data\":[{\"type\":\"qemu\",\"vmid\":1,\"node\":\"n\",\"template\":1},{\"type\":\"qemu\",\"vmid\":2,\"node\":\"n\",\"template\":true},{\"type\":\"qemu\",\"vmid\":3,\"node\":\"n\",\"template\":\"1\"},{\"type\":\"qemu\",\"vmid\":4,\"node\":\"n\",\"template\":0},{\"type\":\"qemu\",\"vmid\":5,\"node\":\"n\"},{\"type\":\"lxc\",\"vmid\":6}]} ")})))) {
 Check((await inventoryApi.ListVmsAsync()).Select(v=>v.Id).SequenceEqual(new[]{4,5}),"Templates not excluded from normal list");
 Check((await inventoryApi.ListVmsAsync(includeTemplates:true)).Count(v=>v.IsTemplate)==3,"Template metadata missing from inventory");
 try {await inventoryApi.ResolveVmAsync(1);throw new Exception("Template resolved for management");}catch(InvalidOperationException){}
}
foreach(var pair in new[]{("qxl","SPICE"),("type=qxl2,memory=64","SPICE"),("qxl3","SPICE"),("qxl4","SPICE"),("std","noVNC"),("virtio","noVNC"),("memory=32","noVNC"),("serial0","noVNC")}) {
 using var doc=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new {vga=pair.Item1}));
 Check(VmConsoleInfo.Parse(doc.RootElement).Resolve("Auto")==pair.Item2,"VGA detection failed: "+pair.Item1);
}
using(var doc=System.Text.Json.JsonDocument.Parse("{}"))Check(VmConsoleInfo.Parse(doc.RootElement).Resolve("Auto")=="noVNC","Default VGA not recognized");
foreach(var info in new[]{new VmConsoleInfo(true,"qxl"),new VmConsoleInfo(false,"none"),new VmConsoleInfo(false,"std")}) {
 try {info.Resolve("SPICE");throw new Exception("Unsupported console accepted");}catch(InvalidOperationException){}
}
Check(new VmConsoleInfo(false,"qxl").Resolve("noVNC")=="noVNC","Manual noVNC override was lost");
using(var detectApi=new ProxmoxApi("https://pve.example.test",new Fake(req=>{
 Check(req.RequestUri!.Query=="?current=1","Pending configuration used for console detection");
 return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"data\":{\"vga\":\"qxl\"}}")});
})))Check((await detectApi.GetConsoleInfoAsync(testVm)).HasSpice,"Current VGA detection failed");
Console.WriteLine("PASS: templates filtered from import/resolve; retained for inventory; QXL variants/default/serial detection; template/headless/manual mismatch guards; current=1 configuration.");
var requiredRights = "\"VM.Audit\":0,\"VM.Console\":0,\"VM.PowerMgmt\":0,\"VM.Config.CPU\":0,\"VM.Config.Memory\":0,\"VM.Config.Options\":0,\"VM.Config.Network\":0";
var testRequests = new List<string>();
using (var probe = new ProxmoxApi("https://pve.example.test", new Fake(req => {
    Check(req.Method == HttpMethod.Get, "Connection test must be read-only");
    Check(req.Headers.GetValues("Authorization").Single() == "PVEAPIToken=test@pve!probe=synthetic-probe-secret", "Probe token missing");
    var path = req.RequestUri!.PathAndQuery; testRequests.Add(path);
    var body = path.Contains("resources") ? "{\"data\":[{\"type\":\"qemu\",\"vmid\":101,\"node\":\"n\",\"name\":\"Full\"},{\"type\":\"qemu\",\"vmid\":102,\"node\":\"n\",\"name\":\"Limited\"},{\"type\":\"qemu\",\"vmid\":103,\"node\":\"n\",\"template\":1}]}" :
        path.Contains("101") ? "{\"data\":{\"/vms/101\":{" + requiredRights + "}}}" :
        path.Contains("102") ? "{\"data\":{\"/vms/102\":{\"VM.Console\":1}}}" :
        "{\"data\":{\"/vms\":{\"VM.Console\":1,\"VM.Audit\":0}}}";
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
}))) {
    probe.UseToken("test@pve!probe", "synthetic-probe-secret");
    var report = await probe.TestConnectionAsync();
    Check(report.Contains("Full application permissions: 1 of 2") && report.Contains("VM 101 (Full): All required"), "Non-propagating per-VM permissions rejected");
    Check(report.Contains("Available: Console") && report.Contains("Not enabled for all VMs: Status"), "Inheritance reporting incorrect");
    Check(report.Contains("VM 102 (Limited): Missing: Status") && report.Contains("VM.Config.Memory"), "Missing rights not listed");
    Check(!report.Contains("synthetic-probe-secret") && !testRequests.Any(p=>p.Contains("103")), "Secret exposed or template checked");
}
using (var emptyProbe = new ProxmoxApi("https://pve.example.test", new Fake(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
    Content = new StringContent(req.RequestUri!.AbsolutePath.Contains("resources") ? "{\"data\":[]}" : "{\"data\":{}}")
})))) Check((await emptyProbe.TestConnectionAsync()).Contains("No VMs were returned."), "Empty inventory reported as fully verified");
foreach (var failure in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden }) {
    using var rejectedProbe = new ProxmoxApi("https://pve.example.test", new Fake(_ => Task.FromResult(new HttpResponseMessage(failure))));
    try { await rejectedProbe.TestConnectionAsync(); throw new Exception("Rejected connection test passed"); }
    catch (HttpRequestException) { }
}
using (var failedPermissions = new ProxmoxApi("https://pve.example.test", new Fake(req => Task.FromResult(
    req.RequestUri!.AbsolutePath.Contains("resources") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") } : new HttpResponseMessage(HttpStatusCode.Forbidden))))) {
    try { await failedPermissions.TestConnectionAsync(); throw new Exception("Permission failure reported as success"); } catch (HttpRequestException) { }
}
Console.WriteLine("PASS: read-only connection test; token header; inherited versus per-VM rights; missing capabilities; template exclusion; empty inventory; authentication and permission failures; secret exclusion.");
var networkRaw="virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr0,tag=30,trunks=40;50,firewall=1,queues=4,rate=10,mtu=9000,future=keep";
var network=new VmNetwork(networkRaw);
Check(network.Connected && network.Bridge=="vmbr0","Default cable state or bridge wrong");
Check(network.Change("vmbr0",true)==networkRaw,"Unchanged NIC would be rewritten");
var changedNetwork=network.Change("vmbr1",false);
Check(changedNetwork==networkRaw.Replace("bridge=vmbr0","bridge=vmbr1")+",link_down=1","Network attributes lost during bridge/link edit");
Check(new VmNetwork(changedNetwork).Change("vmbr0",true)==networkRaw,"Reconnect did not preserve NIC attributes");
Check(!new VmNetwork("e1000=AA:BB:CC:DD:EE:FF,link_down=1").Connected,"Disconnected NIC shown connected");
Check(new VmNetwork(network.Change("",true)).Bridge=="","Blank bridge not removed");
Check(VmNetwork.Equivalent(networkRaw,string.Join(",",networkRaw.Split(',').Reverse())+",link_down=0"),"Reordered/default readback not recognized");
Check(!VmNetwork.Equivalent(networkRaw,changedNetwork),"Different network readback accepted");
foreach(var invalid in new[]{"vmbr0,tag=99","bridge with spaces","vmbr0\n"}) {
    if(invalid.EndsWith('\n'))continue; // UI whitespace trim is intentional.
    try { network.Change(invalid,true);throw new Exception("Invalid bridge accepted"); }catch(ArgumentException){}
}
await management.UpdateConfigurationAsync(testVm,baseline.Digest,new Dictionary<string,string>{{"net0",changedNetwork}});
Check(managementCalls.Last().Body.Contains("net0=") && managementCalls.Last().Body.Contains("digest="),"Network write missing digest or interface");
foreach(var invalidKey in new[]{"net-1","net0.foo","network","net01"}) {
    try {await management.UpdateConfigurationAsync(testVm,baseline.Digest,new Dictionary<string,string>{{invalidKey,networkRaw}});throw new Exception("Invalid network key allowed");}catch(ArgumentException){}
}
using(var currentNetworkApi=new ProxmoxApi("https://pve.example.test",new Fake(req=>{
    var permission=req.RequestUri!.AbsolutePath.EndsWith("permissions");
    if(!permission)Check(req.RequestUri.Query=="?current=1","Active configuration request omitted current=1");
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(permission?"{\"data\":{\"/vms/101\":{\"VM.Config.Network\":0}}}":"{\"data\":{\"net0\":\"virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr0,link_down=1\"}}")});
}))) {
    Check(!(new VmNetwork((await currentNetworkApi.GetConfigurationAsync(testVm,current:true)).Get("net0"))).Connected,"Active network config not parsed");
    Check(await currentNetworkApi.CanEditNetworkAsync(101),"Non-propagating network privilege ignored");
}
Console.WriteLine("PASS: network link/bridge parsing, changed-only editing, unknown/VLAN/MAC/firewall attribute preservation, normalized readback, digest, network-key and bridge validation, current config endpoint and network permission.");
var nestedError=new System.Net.Http.HttpRequestException("SSL connection failed: PVEAPIToken=user@pve!client=hidden-token",new System.Security.Authentication.AuthenticationException("Certificate chain: UntrustedRoot; password=hidden-password; secret-in-message"));
var readable=ErrorText.Format(nestedError,new[]{"secret-in-message"});
Check(readable.Contains("UntrustedRoot")&&readable.Contains("AuthenticationException")&&!readable.Contains("hidden-token")&&!readable.Contains("hidden-password")&&!readable.Contains("secret-in-message"),"Inner exception or redaction failed");
var aggregateDetails=ErrorText.Format(new AggregateException(new Exception("first cause"),new Exception("second cause")));
Check(aggregateDetails.Contains("first cause")&&aggregateDetails.Contains("second cause"),"Aggregate causes lost");
Console.WriteLine("PASS: nested TLS/aggregate error causes and credential redaction.");
await SetupChecks.Run();
await LifecycleChecks.Run();
await SerialChecks.Run();
await BridgeChecks.Run();
sealed class Fake(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
}
















