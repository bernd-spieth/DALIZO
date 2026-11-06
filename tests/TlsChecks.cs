// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ProxmoxSpice;
static class TlsChecks
{
    public static async Task Run(){
        using var key=RSA.Create(2048);
        var request=new CertificateRequest("CN=localhost",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder();san.AddIpAddress(IPAddress.Loopback);request.CertificateExtensions.Add(san.Build());
        using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
        using var certificate=X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
        using(var strict=TlsPolicy.Handler(new Uri("https://pve.example.test:8006"),false))
            Require(strict.ServerCertificateCustomValidationCallback!=null&&!strict.AllowAutoRedirect,"Strict HTTPS defaults changed");
        using(var relaxed=TlsPolicy.Handler(new Uri("https://pve.example.test:8006"),true)){
            var validate=relaxed.ServerCertificateCustomValidationCallback!;
            Require(validate(new(HttpMethod.Get,"https://pve.example.test:8006/"),certificate,null,SslPolicyErrors.RemoteCertificateChainErrors),"Opt-in failed");
            foreach(var other in new[]{"https://other.example.test:8006/","https://pve.example.test:8007/","http://pve.example.test:8006/"})
                Require(!validate(new(HttpMethod.Get,other),certificate,null,SslPolicyErrors.RemoteCertificateChainErrors),"Exception crossed server boundary");
        }
        using(var socket=new ClientWebSocket()){
            TlsPolicy.Configure(socket.Options,false);Require(socket.Options.RemoteCertificateValidationCallback!=null,"WebSocket strict default changed");
            TlsPolicy.Configure(socket.Options,true);Require(socket.Options.RemoteCertificateValidationCallback!(socket,certificate,null,SslPolicyErrors.RemoteCertificateChainErrors),"WebSocket opt-in missing");
        }
        foreach(bool accept in new[]{false,true}){
            List<bool> attempts=[];int offers=0;
            try{
                var ok=await TlsPolicy.Connect(false,skip=>{attempts.Add(skip);return skip?Task.FromResult(true):Task.FromException<bool>(new HttpRequestException("TLS",new TlsPolicy.CertificateRejected("PartialChain")));},
                    error=>{offers++;return accept;});
                Require(accept&&ok,"Unexpected successful retry");
            }catch(HttpRequestException) when(!accept){}
            Require(offers==1&&attempts.SequenceEqual(accept?new[]{false,true}:new[]{false}),"Consent/retry mismatch");
        }
        foreach(var unrelated in new Exception[]{new HttpRequestException("Unauthorized"),new System.Security.Authentication.AuthenticationException("Protocol mismatch"),new OperationCanceledException()}){
            try{await TlsPolicy.Connect(false,_=>Task.FromException<bool>(unrelated),_=>throw new Exception("Prompted for a non-certificate error"));}
            catch(Exception e) when(e==unrelated){}
        }
        var profile=new ServerProfile(Guid.NewGuid(),"Test","https://pve.example.test:8006","test@pve!client","",null);
        Require(!profile.IgnoreCertificateErrors,"Profile default is unsafe");
        var legacy=JsonSerializer.SerializeToNode(profile)!;legacy.AsObject().Remove("IgnoreCertificateErrors");
        Require(!legacy.Deserialize<ServerProfile>()!.IgnoreCertificateErrors,"Old profiles must remain strict");
        var path=Path.Combine(AppContext.BaseDirectory,"dalizo-tls-"+Guid.NewGuid()+".json");
        try{var store=new ProfileStore(path);store.Save(new(1,new(){profile with{IgnoreCertificateErrors=true}},new()));Require(store.Load().Servers[0].IgnoreCertificateErrors,"Exception not persisted");}
        finally{if(File.Exists(path))File.Delete(path);}
        foreach(bool allow in new[]{false,true,false}){
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            var port=((IPEndPoint)listener.LocalEndpoint).Port;bool received=false;
            var serving=Task.Run(async()=>{
                using var client=await listener.AcceptTcpClientAsync(timeout.Token);using var tls=new SslStream(client.GetStream(),false);
                try{
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=certificate},timeout.Token);
                    using var reader=new StreamReader(tls,Encoding.ASCII,false,1024,true);
                    string? line;while(!string.IsNullOrEmpty(line=await reader.ReadLineAsync(timeout.Token)))if(line.StartsWith("Authorization:",StringComparison.OrdinalIgnoreCase))received=true;
                    var body="{\"data\":[]}";var reply=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\nConnection: close\r\n\r\n"+body);
                    await tls.WriteAsync(reply,timeout.Token);
                }catch(System.Security.Authentication.AuthenticationException) when(!allow){}catch(IOException) when(!allow){}
            });
            bool accepted=false;
            try{using var api=new ProxmoxApi($"https://127.0.0.1:{port}",ignoreCertificateErrors:allow);api.UseToken("test@pve!client","synthetic-secret");await api.ListVmsAsync(timeout.Token);accepted=true;}
            catch(HttpRequestException e) when(!allow){Require(TlsPolicy.Rejection(e)!=null,"Real certificate rejection not recognized");}
            finally{await serving;listener.Stop();}
            Require(accepted==allow&&received==allow,"Local TLS enforcement or credential boundary failed");
        }
        Console.WriteLine("PASS: real loopback TLS rejects untrusted certificates by default, accepts explicit opt-in, stays strict for other clients; origin scope, WebSocket policy and profile defaults/persistence.");
    }
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
}
