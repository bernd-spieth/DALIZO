// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProxmoxSpice;

public partial class MainWindow
{
    const string AgentHelp = "If missing, install or start SPICE Guest Tools in Windows, or spice-vdagent in Linux, inside the VM. Check the SPICE agent channel and the guest desktop session. Installation alone does not guarantee availability.";
    internal static (string State, string Help) DescribeFeature(bool connected, bool spice, bool enabled, bool agent, bool supported, bool resize)
    {
        if (!connected) return ("Not checked", "Connect this VM's console to check feature availability.");
        if (!enabled) return ("Disabled", "Enable this feature in VM settings > Console. Support will be checked while the console is connected.");
        if (!spice && resize) return ("Not verified", "Automatic resize requests are enabled. The VNC server must support and accept guest resolution changes; acceptance is not confirmed by DALIZO. Image scaling remains available as a fallback. Installing a guest agent alone does not guarantee VNC resize support.");
        if (!spice) return ("Not verified", "Text clipboard transfer is enabled, but this noVNC session does not confirm guest clipboard support. Check Proxmox VNC clipboard configuration and the guest SPICE agent. Use VM actions > Send text to VM to send text.");
        if (!agent) return ("Agent unavailable", "No connected SPICE guest agent was detected. " + AgentHelp);
        if (!supported) return ("Not advertised", "The connected SPICE guest agent has not advertised this capability. Check or update the guest tools and drivers. " + AgentHelp);
        return ("Supported", "The connected SPICE guest agent advertises this capability. " + (resize ? "Guest display drivers and desktop settings can still limit resolution changes." : "Guest session settings or policies can still prevent clipboard transfer."));
    }
    event Action? ConsoleFeaturesChanged;
    void UpdateFeatureIndicators() => ConsoleFeaturesChanged?.Invoke();
}
