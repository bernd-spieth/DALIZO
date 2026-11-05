// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
namespace ProxmoxSpice;
public partial class MainWindow
{
    void RemoveServer(object sender,RoutedEventArgs e)=>Guard(()=> {
        if(Servers.SelectedItem is not ServerProfile server)return;
        Window owner=sender as Window??this;
        var dialog=new EditorDialog(owner,"Remove server","Remove profile","Cancel",false);
        dialog.Note($"Remove local server profile '{server.Name}' and its saved VM entries? Proxmox VMs are not deleted.");
        bool managed=ProxmoxSetup.TryManagedAccess(server.TokenId,out var user,out var role);
        bool duplicate=data.Servers.Any(s=>s.Id!=server.Id&&s.TokenId==server.TokenId);
        var cleanup=dialog.Check("Also remove this connection’s access in Proxmox",false);
        cleanup.IsEnabled=managed&&!duplicate;
        if(!managed)dialog.Note("DALIZO did not create this account, so it can only remove the saved connection. Ask your administrator to remove access in Proxmox if needed.");
        else if(duplicate)dialog.Note("Another saved server uses the same login key. Its Proxmox access must be kept so that connection can still work.");
        else dialog.Note($"You can also remove the access DALIZO created in Proxmox. Sign in as an administrator and review the changes first. Accounts used by other connections are kept.");
        dialog.Note("Removing the Proxmox login key also disconnects any other installation using that key. Restoring a DALIZO backup will not restore the key.");
        if(dialog.ShowDialog()!=true)return;
        string? cleaned=null;
        if(cleanup.IsChecked==true) {
            cleaned=RemoveRemoteAccess(owner,server);
            if(cleaned==null)return;
        }
        var next=Snapshot();next.Vms.RemoveAll(v=>v.ServerId==server.Id);next.Servers.RemoveAll(s=>s.Id==server.Id);
        Commit(next);sessionSecrets.Remove(server.Id);
        Status(cleaned==null?"Server profile removed. Proxmox access was kept.":"Server profile removed. "+cleaned);
    });
    string? RemoveRemoteAccess(Window owner,ServerProfile server)
    {
        var dialog=new EditorDialog(owner,"Remove DALIZO access","Inspect access","Cancel",false);
        dialog.Note("Server: "+server.Address+"\nToken: "+server.TokenId);
        var admin=dialog.Text("Proxmox administrator username","root@pam");
        var password=dialog.Password("Administrator password (not saved)");
        dialog.Note("First, check what can be removed. Nothing is deleted until you confirm the next step. If removal fails, the saved connection is kept so you can try again.");
        string? result=null;
        dialog.ValidateAsync=async()=> {
            using var api=new ProxmoxSetup(server.Address,ignoreCertificateErrors:server.IgnoreCertificateErrors);
            string pass=password.Password;password.Clear();
            try {
                await api.LoginAsync(admin.Text.Trim(),pass,()=>{
                    var factor=new EditorDialog(dialog,"Two-factor authentication","Continue");var code=factor.Password("TOTP code");
                    string? answer=factor.ShowDialog()==true?code.Password.Trim():null;code.Clear();return Task.FromResult(answer);
                });
            }finally{pass="";}
            var preview=await api.InspectCleanupAsync(server.TokenId);
            var confirm=new EditorDialog(dialog,"Confirm access removal","Remove access","Cancel",false);
            confirm.Note("Server: "+server.Address);
            confirm.Note(preview.RemoveAccount?"Remove this login key and the DALIZO account and permissions created for it.":"Remove this login key only. Keep the account and its permissions because they are shared or have been changed.");
            confirm.Details(preview.Description);
            confirm.Note("Every installation using this login key will lose access. Your VMs will not be deleted. Please do not change permissions in Proxmox during removal.");
            if(confirm.ShowDialog()!=true)return false;
            result=await api.CleanupAsync(preview);return true;
        };
        try{return dialog.ShowDialog()==true?result:null;}finally{password.Clear();}
    }
}
