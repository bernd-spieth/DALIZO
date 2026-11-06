// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
namespace ProxmoxSpice;
internal sealed class ErrorDetails : Expander
{
    readonly TextBox text=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MaxHeight=200,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,6,0,6)};
    public ErrorDetails(){Header="Show error details";Visibility=Visibility.Collapsed;Margin=new Thickness(0,6,0,8);var panel=new StackPanel();panel.Children.Add(text);panel.Children.Add(new TextBlock{Text="Review server names and local paths before sharing these details.",TextWrapping=TextWrapping.Wrap});var copy=new Button{Content="Copy details",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,6,0,0)};copy.Click+=(_,_)=>{try{Clipboard.SetText(text.Text);}catch{copy.Content="Clipboard busy — try again";}};panel.Children.Add(copy);Content=panel;}
    public void Set(Exception error,IEnumerable<string>? secrets=null){text.Text=ErrorText.Format(error,secrets);IsExpanded=false;Visibility=Visibility.Visible;}
    public void Clear(){text.Clear();Visibility=Visibility.Collapsed;IsExpanded=false;}
    public static void Show(Window? owner,string message,Exception error,IEnumerable<string>? secrets=null){ShowText(owner,message,ErrorText.Format(error,secrets));}
    public static void ShowText(Window? owner,string message,string details,Action? review=null){
        var window=new Window{Title="DALIZO — Error details",Width=620,SizeToContent=SizeToContent.Height,MaxHeight=SystemParameters.WorkArea.Height-40,WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner};
        if(owner?.IsVisible==true)window.Owner=owner;
        var panel=new StackPanel{Margin=new Thickness(20)};panel.Children.Add(new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap});var view=new ErrorDetails();view.text.Text=details;view.Visibility=Visibility.Visible;panel.Children.Add(view);
        if(review!=null){var action=new Button{Content="Review certificate failure…",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,4,0,6)};action.Click+=(_,_)=>{window.Close();review();};panel.Children.Add(action);}
        var close=new Button{Content="Close",IsCancel=true,IsDefault=true,HorizontalAlignment=HorizontalAlignment.Right,Padding=new Thickness(14,6,14,6)};close.Click+=(_,_)=>window.Close();panel.Children.Add(close);window.Content=panel;window.ShowDialog();
    }
}
public partial class MainWindow
{
    string? lastErrorDetails;
    Action? lastErrorReview;
    (Guid Id,string Address,string Reason)? lastCertificateNotice;
    void InventoryCertificateFailure(ServerProfile server,Exception error,TlsPolicy.CertificateRejected rejection){
        var notice=(server.Id,server.Address,rejection.Message);
        if(lastCertificateNotice==notice)return;
        ReportError("Certificate verification failed for "+server.Name+".",error);
        lastCertificateNotice=notice;
        lastErrorReview=()=>{
            if(!closing && data.Servers.Contains(server) && OfferCertificateRetry(this,server.Address,rejection,true)){
                var next=Snapshot();var idx=next.Servers.IndexOf(server);if(idx<0)return;
                next.Servers[idx]=server with{IgnoreCertificateErrors=true};Commit(next);lastInventorySync.Remove(server.Id);
                lastCertificateNotice=null;lastErrorReview=null;_=PollStatus();
            }
        };
    }
    void ReportError(string context,Exception error){if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(new Action(()=>ReportError(context,error)));return;}if(closing)return;lastErrorReview=null;lastCertificateNotice=null;lastErrorDetails=ErrorText.Format(error,sessionSecrets.Values);LastErrorOption.IsEnabled=true;Status(context+" Open Options > Last error details for more information.");}
    void ShowLastError(object sender,RoutedEventArgs e){OptionsToggle.IsChecked=false;if(lastErrorDetails!=null)ErrorDetails.ShowText(this,"Most recent error",lastErrorDetails,lastErrorReview);}
}
