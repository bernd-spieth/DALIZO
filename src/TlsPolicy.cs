// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Net.WebSockets;
namespace ProxmoxSpice;
internal static class TlsPolicy
{
    public static HttpClientHandler Handler(Uri server,bool ignoreCertificateErrors,bool useCookies=true){
        var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=useCookies};
        if(ignoreCertificateErrors)handler.ServerCertificateCustomValidationCallback=(request,certificate,chain,errors)=>
            certificate!=null && request.RequestUri is Uri target && target.Scheme=="https" &&
            string.Equals(target.IdnHost,server.IdnHost,StringComparison.OrdinalIgnoreCase) && target.Port==server.Port;
        return handler;
    }
    public static void Configure(ClientWebSocketOptions options,bool ignoreCertificateErrors){
        if(ignoreCertificateErrors)options.RemoteCertificateValidationCallback=(_,certificate,_,_)=>certificate!=null;
    }
}
