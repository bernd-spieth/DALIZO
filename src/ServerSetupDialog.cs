// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;

namespace ProxmoxSpice;
public partial class MainWindow
{
    void AddAdministratorSetup(EditorDialog owner,TextBox address,TextBox token,PasswordBox secret,CheckBox remember)
    {
        var start=new Button {Content="Set up access with administrator…",Margin=new Thickness(0,12,0,0),Padding=new Thickness(10,6,10,6)};
        owner.AddField(start);
        start.Click+=(_,_)=> {
            try {
                var result=ShowAdministratorSetup(owner,address.Text.Trim());
                if(result==null)return;
                token.Text=result.TokenId;secret.Password=result.Secret;remember.IsChecked=true;
                owner.Note("Access created and verified. Click Apply to save this server.");start.IsEnabled=false;
            } catch(Exception error) {ErrorDetails.Show(owner,"Access setup failed.",error,new[]{secret.Password});}
        };
    }
    SetupResult? ShowAdministratorSetup(Window owner,string address,Action<SetupResult>? save=null)
    {
                using(var check=new ProxmoxApi(address)){}
                var dialog=new EditorDialog(owner,"Automatic Proxmox access setup",save==null?"Create access":"Create access and add server");
                dialog.Note("Server: "+address);
                var admin=dialog.Text("Proxmox administrator username","root@pam");
                dialog.Help("Use an administrator account, for example root@pam or admin@pve. Include the part after @.");
                var password=dialog.Password("Administrator password (not saved)");
                var all=dialog.Check("Access to all current and future VMs",true);
                var ids=dialog.Text("VM IDs (if you did not select all VMs)");ids.IsEnabled=false; dialog.Help("Enter the VM numbers shown in Proxmox, separated by commas. Example: 101, 102.");
                all.Checked+=(_,_)=>ids.IsEnabled=false;all.Unchecked+=(_,_)=>ids.IsEnabled=true;
                var initial=SetupPlan.Create(true,"");
                var planNote=new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};
                dialog.AddField(planNote);
                void UpdatePreview() {
                    planNote.Text="DALIZO will set up its own connection to Proxmox.\n"
                        +(all.IsChecked==true?"VM access: all current and future VMs.":"VM access: only the VM numbers entered above.");
                }
                UpdatePreview();all.Checked+=(_,_)=>UpdatePreview();all.Unchecked+=(_,_)=>UpdatePreview();
                dialog.Note("DALIZO will be able to open consoles, start and stop the selected VMs, and change their CPU, memory, name and network settings. It can connect them to networks in all Proxmox SDN zones, but cannot change those networks.");
                dialog.Help("DALIZO uses its own login key, not your administrator password. This key stays valid until you remove it. Existing accounts keep their current settings.");
                dialog.Note(save==null ? "Create access sets up the connection in Proxmox. Then click Apply to save it in DALIZO. Cancelling afterwards does not undo the setup in Proxmox." : "DALIZO will set up the connection, check it and save the server."); dialog.Help("Your administrator password is not saved. If your account requires an authenticator code, you will be prompted for it. For security-key or browser-based sign-in, use Manual setup with an existing API token.");
                var reuse=dialog.Check("Reuse a suitable DALIZO account if one already exists",true);
                dialog.Help("If a suitable account is found, you can reuse it. DALIZO creates a separate login key for this connection; other connections keep their keys.");
                dialog.Details($"New account: {initial.User}\nRole: {initial.Role}\nToken: {initial.FullTokenId}\nThe API token has privilege separation enabled and no expiry. The account has no login password. VM permissions apply at /vms or the selected /vms/<id> paths. SDN.Use applies at /sdn/zones with inheritance for both user and token. A matching existing account can be selected below after sign-in.");
                SetupResult? result=null;
                dialog.ValidateAsync=async()=> {
                    var scoped=SetupPlan.Create(all.IsChecked==true,ids.Text);
                    var plan=initial with {Paths=scoped.Paths};
                    string pass=password.Password;password.Clear();
                    using var setup=new ProxmoxSetup(address);
                    try {
                        await setup.LoginAsync(admin.Text.Trim(),pass,()=> {
                            var factor=new EditorDialog(dialog,"Two-factor authentication","Continue");
                            factor.Note("Enter the current code from your authenticator app. Security keys and browser-based sign-in are not available here.");
                            var code=factor.Password("TOTP code");
                            string? answer=factor.ShowDialog()==true?code.Password.Trim():null;
                            code.Clear();return Task.FromResult(answer);
                        });
                    } finally {pass="";}
                    bool reuseAccount=false;
                    if(reuse.IsChecked==true) {
                        var candidates=await setup.FindReusableAsync(plan.Paths);
                        if(candidates.Length>0) {
                            var select=new EditorDialog(dialog,"Existing DALIZO access","Continue");
                            select.Note("A suitable DALIZO account already exists. You can reuse it or create a new one. Other connections keep working.");
                            var options=new object[]{"Create a new dedicated account and role"}.Concat(candidates.Select(p=>(object)("Reuse "+p.User+" / "+p.Role))).ToArray();
                            var choice=select.Choice("Access to use",options,options[1]);choice.DisplayMemberPath="";
                            select.Note("Another DALIZO installation may use this account too. When removing this connection later, DALIZO keeps accounts that are still shared.");
                            if(select.ShowDialog()!=true)return false;
                            if(choice.SelectedIndex>0){plan=candidates[choice.SelectedIndex-1];reuseAccount=true;}
                        }
                    }
                    async Task Verify(SetupResult generated) {
                        using var verify=new ProxmoxApi(address);
                        verify.UseToken(generated.TokenId,generated.Secret);
                        await verify.VerifySetupPermissionsAsync(plan.Paths);
                        save?.Invoke(generated);
                    }
                    result=reuseAccount?await setup.ProvisionReusedAsync(plan,Verify):await setup.ProvisionAsync(plan,Verify);
                    return true;
                };
        try {return dialog.ShowDialog()==true ? result : null;}
        finally {password.Clear();}
    }
}

