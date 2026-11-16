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
    public static readonly string[] Models=["virtio","e1000","e1000e","rtl8139","vmxnet3"];
    static readonly string[] LegacyModels=["i82551","i82557b","i82559er","ne2k_isa","ne2k_pci","pcnet"];
    public string Model=>Fields.Keys.FirstOrDefault(k=>Models.Contains(k)||LegacyModels.Contains(k))??Raw.Split(',')[0].Split('=')[0];
    public bool Firewall=>Get("firewall") is "1" or "on" or "yes" or "true";
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
        // Proxmox assigns the MAC when a new adapter specifies only its model.
        foreach(var model in Models.Concat(LegacyModels))
            if(bf.TryGetValue(model,out var requested) && requested=="" && af.TryGetValue(model,out var assigned) &&
                Regex.IsMatch(assigned,@"^(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$"))af[model]="";
        af.Remove("firewall");bf.Remove("firewall");
        return a.Firewall==b.Firewall && a.Connected==b.Connected && af.Count==bf.Count && af.All(p=>bf.TryGetValue(p.Key,out var value)&&
            (p.Value==value || (Models.Contains(p.Key)||LegacyModels.Contains(p.Key)) && string.Equals(p.Value,value,StringComparison.OrdinalIgnoreCase)));
    }
    public string ChangeAdapter(string bridge,bool connected,string model,string mac,string vlan,bool firewall,string rate,string queues,string mtu){
        const string macPattern=@"^(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$";
        if(model!=Model && !Models.Contains(model))throw new ArgumentException("Choose a supported adapter model.");
        mac=mac.Trim();
        if(mac!=Get(Model) && mac!="" && !Regex.IsMatch(mac,macPattern))throw new ArgumentException("Enter a MAC address such as 02:00:00:12:34:56.");
        var parts=Change(bridge,connected).Split(',').ToList();
        if(model!=Model || mac!=Get(Model)){
            var index=parts.FindIndex(p=>p.Split('=')[0]==Model);
            parts[index]=mac==""?model:model+"="+mac.ToUpperInvariant();
        }
        void Field(string key,string value){
            if(Get(key)==value)return;
            parts.RemoveAll(p=>p.StartsWith(key+"=",StringComparison.Ordinal));
            if(value!="")parts.Add(key+"="+value);
        }
        string Integer(string key,string value,int min,int max,bool inherit=false){
            value=value.Trim();
            if(value=="" || value==Get(key))return value;
            if(!int.TryParse(value,out int n)||n>max||(n<min && !(inherit && n==1)))throw new ArgumentException($"Enter {key} between {min} and {max}"+(inherit?", or 1 to inherit the bridge MTU.":"."));
            return n.ToString();
        }
        Field("tag",Integer("tag",vlan,1,4094));
        Field("queues",Integer("queues",queues,1,64));
        Field("mtu",Integer("mtu",mtu,576,65520,true));
        rate=rate.Trim();
        if(rate!="" && rate!=Get("rate")){
            var culture=System.Globalization.CultureInfo.InvariantCulture;
            if(!decimal.TryParse(rate.Replace(',','.'),System.Globalization.NumberStyles.AllowDecimalPoint,culture,out var limit)||limit>10240)
                throw new ArgumentException("Enter a rate limit between 0 and 10240 MB/s.");
            rate=limit==0?"":limit.ToString("0.############################",culture);
        }
        Field("rate",rate);
        if(firewall!=Firewall)Field("firewall",firewall?"1":"");
        return string.Join(",",parts);
    }
}
