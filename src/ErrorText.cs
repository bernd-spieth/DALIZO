// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using System.Text.RegularExpressions;
namespace ProxmoxSpice;
internal static class ErrorText
{
    public static string Format(Exception error,IEnumerable<string>? secrets=null)
    {
        var hidden=(secrets??Array.Empty<string>()).Where(s=>!string.IsNullOrEmpty(s)).Distinct().OrderByDescending(s=>s.Length).ToArray();
        string Clean(string value){
            foreach(var secret in hidden){value=value.Replace(secret,"[redacted]",StringComparison.Ordinal);value=value.Replace(Uri.EscapeDataString(secret),"[redacted]",StringComparison.Ordinal);}
            value=Regex.Replace(value,@"(?i)(PVEAPIToken=)[^\s]+","$1[redacted]");
            value=Regex.Replace(value,@"(?im)(Authorization|Cookie|Set-Cookie)\s*:[^\r\n]*","$1: [redacted]");
            value=Regex.Replace(value,@"(?i)(password|secret|ticket|vncticket|token|otp|code)=([^\s&]+)","$1=[redacted]");
            value=Regex.Replace(value,@"(https?://)[^/\s@]+@","$1[redacted]@");
            return value.Length>6000?value[..6000]+"…":value;
        }
        var result=new StringBuilder();var seen=new HashSet<Exception>();
        void Add(Exception e,int depth){if(depth>12||!seen.Add(e))return;result.AppendLine(new string(' ',depth*2)+e.GetType().FullName+" (0x"+e.HResult.ToString("X8")+")");result.AppendLine(Clean(e.Message));
            if(e is AggregateException aggregate){foreach(var inner in aggregate.InnerExceptions)Add(inner,depth+1);}else if(e.InnerException!=null)Add(e.InnerException,depth+1);
        }
        Add(error,0);return result.ToString();
    }
}
