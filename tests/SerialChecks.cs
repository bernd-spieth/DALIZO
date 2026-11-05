// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ProxmoxSpice;
static class SerialChecks
{
 public static async Task Run(){
  var vm=new Vm(101,"node name","Linux","running");int calls=0;
  using var api=new ProxmoxApi("https://example.test:8006",new Handler(async request=>{
    if(request.Headers.GetValues("Authorization").Single()!="PVEAPIToken=test@pve!client=secret")throw new Exception("Serial API token header missing");
    if(!request.RequestUri!.AbsolutePath.EndsWith("/qemu/101/termproxy")||request.Method!=HttpMethod.Post)throw new Exception("Wrong serial endpoint");
    var body=await request.Content!.ReadAsStringAsync();if(body!="serial=serial2")throw new Exception("Wrong serial device");calls++;
    return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{data=new{user="test@pve!client",ticket="PVE:ticket+/="+calls,port="5901"}}))};
  }));api.UseToken("test@pve!client","secret");
  var first=await api.GetSerialTicketAsync(vm,"serial2");var second=await api.GetSerialTicketAsync(vm,"serial2");
  if(first.Ticket==second.Ticket||calls!=2)throw new Exception("Serial ticket reused");
  var uri=api.VncWebSocketUri(vm,first);if(uri.Scheme!="wss"||!uri.Query.Contains("%2B")||!uri.AbsolutePath.EndsWith("/vncwebsocket"))throw new Exception("Wrong serial WebSocket URL");
  try{await api.GetSerialTicketAsync(vm,"serial4");throw new Exception("Invalid serial accepted");}catch(ArgumentException){}
  foreach(var json in new[]{"{\"vga\":\"none\",\"serial0\":\"socket\"}","{\"vga\":\"qxl\",\"serial0\":\"socket\"}"}){
    using var doc=JsonDocument.Parse(json);var info=VmConsoleInfo.Parse(doc.RootElement);if(info.Resolve("Serial terminal")!="Serial terminal"||!info.SerialPorts.Contains("serial0"))throw new Exception("Serial display detection failed");
  }
  using(var doc=JsonDocument.Parse("{\"serial0\":\"/dev/ttyS0\"}")){try{VmConsoleInfo.Parse(doc.RootElement).Resolve("Serial terminal");throw new Exception("Host serial passthrough accepted");}catch(InvalidOperationException){}}
  using(var doc=JsonDocument.Parse("{\"template\":1,\"serial0\":\"socket\"}")){try{VmConsoleInfo.Parse(doc.RootElement).Resolve("Serial terminal");throw new Exception("Serial template accepted");}catch(InvalidOperationException){}}
  foreach(var invalid in new[]{new{user="bad\nuser",ticket="ticket",port=5901},new{user="test@pve!client",ticket="bad\nticket",port=5901},new{user="test@pve!client",ticket="ticket",port=6000}}){
    using var bad=new ProxmoxApi("https://example.test",new Handler(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{data=invalid}))})));bad.UseToken("test@pve!client","secret");
    try{await bad.GetSerialTicketAsync(vm,"serial0");throw new Exception("Invalid serial response accepted");}catch(InvalidOperationException){}
  }
  Console.WriteLine("PASS: serial termproxy/API-token header, fresh tickets, WSS URL, serial device validation, headless/QXL serial selection, template/passthrough rejection and unsafe ticket rejection. Synthetic HTTP only.");
 }
 sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> respond):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>respond(r);}
}
