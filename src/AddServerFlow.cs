// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;

namespace ProxmoxSpice;
public partial class MainWindow
{
    void ChooseServerSetup(Window? owner=null)
    {
        var choice=new EditorDialog(owner ?? this,"Add server","Continue");
        choice.Note("How would you like to add this Proxmox server?");
        var automatic=new RadioButton {Content="Automatic — sign in as an administrator",IsChecked=true,Margin=new Thickness(0,14,0,0),GroupName="SetupMode"};
        choice.AddField(automatic);
        choice.Help("DALIZO sets up its own access to Proxmox and saves the server for you.");
        var manual=new RadioButton {Content="Manual — use an existing API token",Margin=new Thickness(0,14,0,0),GroupName="SetupMode"};
        choice.AddField(manual);
        choice.Help("Use an API token you already have. An API token is a login key for applications.");
        if(choice.ShowDialog()!=true)return;
        if(automatic.IsChecked==true)AddServerAutomatically(owner);else EditServerProfile(null,owner);
    }
    void AddServerAutomatically(Window? owner=null)
    {
        var dialog=new EditorDialog(owner ?? this,"Add server automatically","Continue");
        var name=dialog.Text("Display name");dialog.Hint("Example: Home lab or Production cluster");
        var address=dialog.Text("Proxmox address (HTTPS)");dialog.Hint("Example: https://pve.example.org:8006");
        var proxy=dialog.Text("SPICE proxy (optional)");dialog.Hint("Example: pve.example.org");dialog.Help("Enter a hostname or IP without a protocol or port. Leave empty to use the server hostname.");
        var remember=dialog.Check("Remember this connection on this Windows account",true);
        dialog.Help("Next, sign in as an administrator and choose which VMs DALIZO may access. DALIZO handles the login key for you.");
        var id=Guid.NewGuid();
        dialog.Validate=()=> {
            var draft=new ServerProfile(id,name.Text.Trim(),address.Text.Trim(),"pending@pve!client",proxy.Text.Trim(),null);
            var next=Snapshot();next.Servers.Add(draft);ProfileStore.Validate(next);
            using var check=new ProxmoxApi(draft.Address);
            if(draft.Proxy!=""&&Uri.CheckHostName(draft.Proxy)==UriHostNameType.Unknown)throw new ArgumentException("Enter a proxy hostname/IP without a port, or leave it empty.");
            return true;
        };
        if(dialog.ShowDialog()!=true)return;
        ShowAdministratorSetup(owner ?? this,address.Text.Trim(),generated=> {
            var server=new ServerProfile(id,name.Text.Trim(),address.Text.Trim(),generated.TokenId,proxy.Text.Trim(),remember.IsChecked==true?SecretProtection.Protect(generated.Secret):null);
            var next=Snapshot();next.Servers.Add(server);Commit(next,server:server.Id);
            sessionSecrets[server.Id]=generated.Secret;
        });
        if(data.Servers.Any(s=>s.Id==id)) {
            lastInventorySync.Remove(id);Status("Server added automatically. The VM list will synchronize now.");
            if(!smokeMode)_=PollStatus();
        }
    }
}
