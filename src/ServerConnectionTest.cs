// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;

namespace ProxmoxSpice;

public partial class MainWindow
{
    void AddConnectionTest(EditorDialog dialog, TextBox address, TextBox token, PasswordBox secret, TextBox proxy)
    {
        var button = new Button { Content = "Test connection", HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 12, 0, 0) };
        var report = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 230,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed };
        dialog.AddField(button); dialog.AddField(report);
        CancellationTokenSource? pending = null;
        bool closed = false;
        int revision = 0;
        void Invalidate()
        {
            revision++; pending?.Cancel();
            if (report.Visibility == Visibility.Visible) report.Text = "Settings changed. Test the connection again.";
        }
        address.TextChanged += (_, _) => Invalidate();
        token.TextChanged += (_, _) => Invalidate();
        secret.PasswordChanged += (_, _) => Invalidate();
        proxy.TextChanged += (_, _) => Invalidate();
        dialog.Closed += (_, _) => { closed = true; pending?.Cancel(); };
        button.Click += async (_, _) =>
        {
            var testedRevision = revision;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            pending = timeout; button.IsEnabled = false;
            report.Visibility = Visibility.Visible; report.Text = "Testing HTTPS connection, API token and VM permissions…";
            try
            {
                using var api = new ProxmoxApi(address.Text.Trim());
                api.UseToken(token.Text.Trim(), secret.Password);
                var result = await api.TestConnectionAsync(timeout.Token);
                if (!closed && revision == testedRevision) report.Text = result;
            }
            catch (Exception error)
            {
                if (!closed && revision == testedRevision)
                    report.Text = error is OperationCanceledException ? "Connection test timed out. Try again." :
                        "Connection or permission check failed. " + error.Message;
            }
            finally { pending = null; if (!closed) button.IsEnabled = true; }
        };
    }
}

