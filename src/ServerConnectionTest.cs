// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;

namespace ProxmoxSpice;

public partial class MainWindow
{
    void AddConnectionTest(EditorDialog dialog, TextBox address, TextBox token, PasswordBox secret, TextBox proxy,CheckBox? ignoreTls=null)
    {
        var button = new Button { Content = "Test connection", HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 12, 0, 0) };
        var report = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 230,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed };
        var details=new ErrorDetails(); dialog.AddField(button); dialog.AddField(report);dialog.AddField(details);
        CancellationTokenSource? pending = null;
        bool closed = false;
        int revision = 0; bool applyingException=false;
        void Invalidate()
        {
            if(applyingException)return; revision++; pending?.Cancel();details.Clear();
            if (report.Visibility == Visibility.Visible) report.Text = "Settings changed. Test the connection again.";
        }
        address.TextChanged += (_, _) => Invalidate();
        token.TextChanged += (_, _) => Invalidate();
        secret.PasswordChanged += (_, _) => Invalidate();
        proxy.TextChanged += (_, _) => Invalidate();
        if(ignoreTls!=null){ignoreTls.Checked+=(_,_)=>Invalidate();ignoreTls.Unchecked+=(_,_)=>Invalidate();}
        dialog.Closed += (_, _) => { closed = true; pending?.Cancel(); };
        button.Click += async (_, _) =>
        {
            var testedRevision = revision;
            button.IsEnabled = false;details.Clear();
            report.Visibility = Visibility.Visible; report.Text = "Testing HTTPS connection, API token and VM permissions…";
            try
            {
                var endpoint=address.Text.Trim();
                var result=await TlsPolicy.Connect(ignoreTls?.IsChecked==true,async skip=>{
                    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));pending=timeout;
                    try{using var api=new ProxmoxApi(endpoint,ignoreCertificateErrors:skip);
                    api.UseToken(token.Text.Trim(),secret.Password);
                    return await api.TestConnectionAsync(timeout.Token);
                    }finally{pending=null;}
                },error=>{
                    if(ignoreTls==null||closed||revision!=testedRevision||!OfferCertificateRetry(dialog,endpoint,error,false))return false;
                    applyingException=true;try{ignoreTls.IsChecked=true;}finally{applyingException=false;}
                    return true;
                });
                if (!closed && revision == testedRevision) report.Text = (ignoreTls?.IsChecked==true?"Certificate validation is disabled for this server.\n\n":"")+result;
            }
            catch (Exception error)
            {
                if (!closed && revision == testedRevision) {
                    details.Set(error,new[]{secret.Password,token.Text});
                    report.Text = error is OperationCanceledException ? "Connection test timed out. Try again." :
                        "Connection or permission check failed. Open the error details below.";
                }
            }
            finally { pending = null; if (!closed) button.IsEnabled = true; }
        };
    }
}

