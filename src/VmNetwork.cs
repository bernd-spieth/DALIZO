// SPDX-License-Identifier: GPL-3.0-only
using System.Text.RegularExpressions;
namespace ProxmoxSpice;

public sealed class VmNetwork(string raw)
{
    public string Raw { get; } = raw;
    Dictionary<string,string> Fields => Raw.Split(',',StringSplitOptions.RemoveEmptyEntries)
        .Select(p=>p.Split('=',2)).ToDictionary(p=>p[0],p=>p.Length==2?p[1]:"");
    public static bool IsKey(string key) => Regex.IsMatch(key,@"^net(?:0|[1-9][0-9]*)$");
    public string Get(string key,string fallback="") => Fields.GetValueOrDefault(key,fallback);
    public string Bridge => Get("bridge");
    public bool Connected => Get("link_down","0") is not ("1" or "true" or "on" or "yes");
    public string Summary => $"{(Connected ? "Connected" : "Disconnected")} · Bridge: {(Bridge==""?"None (user-mode network)":Bridge)}";
    public string Details => $"VLAN: {Get("tag","None")} · Firewall: {Get("firewall","0")} · Adapter: {Raw.Split(',')[0]}";
    public string Change(string bridge,bool connected)
    {
        bridge=bridge.Trim();
        if(bridge!="" && !Regex.IsMatch(bridge,@"^[a-zA-Z0-9][a-zA-Z0-9_.:-]{0,63}$"))
            throw new ArgumentException("Enter a bridge name without spaces, commas or control characters.");
        var parts=Raw.Split(',').ToList();
        void Set(string key,string? value) {
            int i=parts.FindIndex(p=>p.StartsWith(key+"=",StringComparison.Ordinal));
            if(i>=0) { if(value==null)parts.RemoveAt(i);else parts[i]=key+"="+value; }
            else if(value!=null)parts.Add(key+"="+value);
        }
        if(bridge!=Bridge)Set("bridge",bridge==""?null:bridge);
        if(connected!=Connected)Set("link_down",connected?null:"1");
        return string.Join(",",parts);
    }
    public static bool Equivalent(string left,string right)
    {
        var a=new VmNetwork(left);var b=new VmNetwork(right);
        var af=a.Fields;var bf=b.Fields;af.Remove("link_down");bf.Remove("link_down");
        return a.Connected==b.Connected && af.Count==bf.Count && af.All(p=>bf.TryGetValue(p.Key,out var value)&&p.Value==value);
    }
}
