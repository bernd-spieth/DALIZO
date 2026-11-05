// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
namespace ProxmoxSpice;
public partial class MainWindow
{
    void RepairServerAccess(Window owner,ServerProfile server)
    {
        if(!ProxmoxSetup.TryManagedAccess(server.TokenId,out _,out _)) {MessageBox.Show(owner,"DALIZO can repair accounts it created itself. For other accounts, use Edit server > Test connection, then ask your Proxmox administrator to add the missing permissions.","Repair permissions");return;}
        var secret=GetSecret(server);if(secret==null)return;
        var dialog=new EditorDialog(owner,"Repair server permissions","Inspect permissions","Cancel",false);
        dialog.Note("Server: "+server.Address+"\nToken: "+server.TokenId);
        dialog.Note("Check what this connection is allowed to do and add missing permissions. You can review the changes first. Your saved connection and existing login key stay the same.");
        var admin=dialog.Text("Proxmox administrator username","root@pam");
        dialog.Hint("Example: root@pam or admin@pve");
        var password=dialog.Password("Administrator password (not saved)");
        dialog.Help("If your account requires an authenticator code, you will be prompted for it. If DALIZO cannot safely identify the account settings, it will ask you to have an administrator check them.");
        bool repaired=false;
        dialog.ValidateAsync=async()=>{
            using var api=new ProxmoxSetup(server.Address,ignoreCertificateErrors:server.IgnoreCertificateErrors);
            string pass=password.Password;password.Clear();
            try {await api.LoginAsync(admin.Text.Trim(),pass,()=>{
                var factor=new EditorDialog(dialog,"Two-factor authentication","Continue");var code=factor.Password("TOTP code");
                string? answer=factor.ShowDialog()==true?code.Password.Trim():null;code.Clear();return Task.FromResult(answer);
            });}finally{pass="";}
            var preview=await api.InspectRepairAsync(server.TokenId);
            var confirm=new EditorDialog(dialog,"Review permission repair",preview.HasChanges?"Add missing permissions":"Verify existing token","Cancel",false);
            confirm.Note(preview.HasChanges?"Add the permissions needed for DALIZO’s VM controls and network selection.":"The required permissions are already in place. Check that the existing login key works.");
            confirm.Note(preview.Paths.Contains("/vms")?"VM access stays set to all current and future VMs.":"VM access stays limited to: "+string.Join(", ",preview.Paths.Where(p=>p.StartsWith("/vms/")).Select(p=>p[5..]))+".");
            confirm.Note("The selected VMs can use networks in all Proxmox SDN zones. The VMs and networks themselves are not changed.");
            if(preview.Shared)confirm.Note("Other connections use this account too. They may also gain the added permissions for VMs they can already access.");
            confirm.Details(preview.Description);
            confirm.Note("Your login key stays the same. Please do not change permissions in Proxmox during this step. If the final check fails, permissions already added remain in place; run this check again to continue.");
            if(confirm.ShowDialog()!=true)return false;
            await api.RepairAccessAsync(preview,async paths=>{
                using var verify=new ProxmoxApi(server.Address,ignoreCertificateErrors:server.IgnoreCertificateErrors);verify.UseToken(server.TokenId,secret);
                await verify.VerifySetupPermissionsAsync(paths);
            });
            repaired=true;return true;
        };
        try{dialog.ShowDialog();}finally{password.Clear();}
        if(repaired){Status("Server permissions verified. Existing profile and token retained.");MessageBox.Show(owner,"Permissions verified with the existing token. Reopen VM Properties to reload the available networks.","Repair permissions",MessageBoxButton.OK,MessageBoxImage.Information);}
    }
}
