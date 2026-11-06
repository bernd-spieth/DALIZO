// SPDX-License-Identifier: GPL-3.0-only
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Net.WebSockets;
namespace ProxmoxSpice;
internal static class TlsPolicy
{
    internal sealed class CertificateRejected(string reason):AuthenticationException(reason);
    static bool Strict(X509Certificate? certificate,X509Chain? chain,SslPolicyErrors errors){
        if(certificate==null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))return false;
        if(errors==SslPolicyErrors.None)return true;
        var flags=chain?.ChainStatus.Select(s=>s.Status.ToString()).Distinct().ToArray()??[];
        throw new CertificateRejected(errors+(flags.Length>0?": "+string.Join(", ",flags):""));
    }
    internal static CertificateRejected? Rejection(Exception error){
        for(Exception? cause=error;cause!=null;cause=cause.InnerException)if(cause is CertificateRejected rejected)return rejected;
        return null;
    }
    internal static async Task<T> Connect<T>(bool ignore,Func<bool,Task<T>> attempt,Func<CertificateRejected,bool> offer){
        try{return await attempt(ignore);}
        catch(Exception error) when(!ignore && Rejection(error)!=null){
            if(!offer(Rejection(error)!))throw;
        }
        return await attempt(true);
    }
    public static HttpClientHandler Handler(Uri server,bool ignoreCertificateErrors,bool useCookies=true){
        var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=useCookies};
        if(ignoreCertificateErrors)handler.ServerCertificateCustomValidationCallback=(request,certificate,chain,errors)=>
            certificate!=null && request.RequestUri is Uri target && target.Scheme=="https" &&
            string.Equals(target.IdnHost,server.IdnHost,StringComparison.OrdinalIgnoreCase) && target.Port==server.Port;
        else handler.ServerCertificateCustomValidationCallback=(_,cert,chain,errors)=>Strict(cert,chain,errors);
        return handler;
    }
    public static void Configure(ClientWebSocketOptions options,bool ignoreCertificateErrors){
        if(ignoreCertificateErrors)options.RemoteCertificateValidationCallback=(_,certificate,_,_)=>certificate!=null;
        else options.RemoteCertificateValidationCallback=(_,cert,chain,errors)=>Strict(cert,chain,errors);
    }
}
