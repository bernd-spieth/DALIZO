// SPDX-License-Identifier: GPL-3.0-only
namespace ProxmoxSpice;

public sealed record SpiceConfig(string Host, string TlsPort, string Password, string Proxy, string Ca, string Subject)
{
    public static SpiceConfig Parse(string text)
    {
        if (text.Length > 131072) throw new FormatException("SPICE configuration too large.");
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        bool section = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[')) { if (line != "[virt-viewer]" || section) throw new FormatException("Invalid SPICE section."); section = true; continue; }
            var split = line.IndexOf('=');
            if (!section || split < 1 || !entries.TryAdd(line[..split], line[(split + 1)..])) throw new FormatException("Invalid SPICE configuration.");
        }
        string Required(string key) => entries.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) && !value.Contains('\0') ? value : throw new FormatException("Incomplete SPICE configuration: " + key);
        if (Required("type") != "spice") throw new FormatException("Not a SPICE configuration.");
        var port = Required("tls-port");
        if (!int.TryParse(port, out var p) || p < 1 || p > 65535) throw new FormatException("Invalid TLS port.");
        var proxy = Required("proxy");
        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.UserInfo != "" || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "") throw new FormatException("Invalid SPICE proxy.");
        var ca = Required("ca").Replace("\\n", "\n").Replace("\\r", "\r");
        if (!ca.Contains("-----BEGIN CERTIFICATE-----")) throw new FormatException("Missing SPICE CA.");
        return new(Required("host"), port, Required("password"), proxy, ca, Required("host-subject"));
    }
}


