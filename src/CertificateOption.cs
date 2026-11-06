// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace ProxmoxSpice;
public partial class MainWindow
{
    static bool OfferCertificateRetry(Window owner,string address,TlsPolicy.CertificateRejected error,bool saved){
        var dialog=new EditorDialog(owner,"Server certificate could not be verified","Ignore and retry","Keep verification",false);
        dialog.Note(address+"\n"+error.Message);
        dialog.Note("The server's identity could not be verified. If you continue, passwords and API tokens could be intercepted.");
        dialog.Note(saved?"This will save the exception for this server profile and retry the connection.":"This enables the exception for this server. It will be saved only if you finish saving the profile.");
        return dialog.ShowDialog()==true;
    }
    static CheckBox AddCertificateOption(EditorDialog dialog,TextBox address,bool enabled){
        var option=dialog.Check("Ignore HTTPS certificate errors (unsafe)",enabled);
        dialog.Help("Applies only to this server's API, noVNC and serial connections. Prefer installing the correct CA certificate. SPICE uses the certificate information in its console ticket.");
        var warning=new TextBlock{Text="The server's identity will not be verified. Passwords and API tokens could be intercepted.",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.Firebrick,Margin=new Thickness(0,4,0,0),Visibility=enabled?Visibility.Visible:Visibility.Collapsed};
        dialog.AddField(warning);
        option.Checked+=(_,_)=>warning.Visibility=Visibility.Visible;
        option.Unchecked+=(_,_)=>warning.Visibility=Visibility.Collapsed;
        address.TextChanged+=(_,_)=>option.IsChecked=false;
        return option;
    }
}
