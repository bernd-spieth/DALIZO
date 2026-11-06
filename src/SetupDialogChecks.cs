// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ProxmoxSpice;
public partial class MainWindow
{
    void CheckCertificateRetryDialog(){
        static IEnumerable<DependencyObject> Walk(DependencyObject root){
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var item in Walk(child))yield return item;}
        }
        foreach(bool acceptRetry in new[]{false,true}){
            bool retryDialogChecked=false;
            _=Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{
                var popup=Application.Current.Windows.OfType<EditorDialog>().Single(w=>w.Title=="Server certificate could not be verified");
                popup.UpdateLayout();var keep=Walk(popup).OfType<Button>().Single(b=>b.Content as string=="Keep verification");
                retryDialogChecked=keep.IsDefault&&Walk(popup).OfType<TextBlock>().Any(t=>t.Text.Contains("https://pve.example.test:8006")&&t.Text.Contains("PartialChain"));
                if(acceptRetry)Walk(popup).OfType<Button>().Single(b=>b.Content as string=="Ignore and retry").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));else popup.Close();
            }));
            if(OfferCertificateRetry(this,"https://pve.example.test:8006",new TlsPolicy.CertificateRejected("PartialChain"),true)!=acceptRetry||!retryDialogChecked)throw new Exception("Certificate retry dialog choice or safe default was incorrect");
        }
    }
    async Task CheckSetupDialogs()
    {
        IEnumerable<Button> Buttons(DependencyObject parent) {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
                var child=VisualTreeHelper.GetChild(parent,i);
                if(child is Button button)yield return button;
                foreach(var nested in Buttons(child))yield return nested;
            }
        }
        var localChecks=await Task.Run(Prerequisites.Check);
        if(localChecks.Length!=3||!localChecks[0].Ready)throw new Exception("Local prerequisite report incomplete");
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"prerequisite-checks.txt"),localChecks.Select(c=>c.Name+": "+c.Ready+" - "+c.Detail));
        var checkTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};bool prerequisitesShown=false;
        checkTimer.Tick+=(_,_)=>{var popup=Application.Current.Windows.OfType<EditorDialog>().FirstOrDefault(w=>w.Title=="Prerequisites");if(popup==null)return;checkTimer.Stop();popup.UpdateLayout();prerequisitesShown=true;
            var preview=new RenderTargetBitmap((int)popup.ActualWidth,(int)popup.ActualHeight,96,96,PixelFormats.Pbgra32);preview.Render(popup);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(preview));using(var file=File.Create(Path.Combine(AppContext.BaseDirectory,"prerequisites-preview.png")))encoder.Save(file);popup.Close();};
        checkTimer.Start();try{ShowPrerequisites(new[]{new ComponentCheck("Windows desktop / .NET",true,"Running"),new ComponentCheck("SPICE",false,"Install VirtViewer x64","https://www.spice-space.org/download.html"),new ComponentCheck("noVNC / Serial",false,"Install WebView2","https://developer.microsoft.com/en-us/microsoft-edge/webview2/")});}finally{checkTimer.Stop();}
        if(!prerequisitesShown)throw new Exception("Missing-component dialog did not open");
        CheckCertificateRetryDialog();
        Exception? helpFailure=null;
        var helpCheck=new EditorDialog(this,"Help and errors check","Submit");
        var input=helpCheck.Text("Server");helpCheck.Help("A helpful server explanation.");
        var hiddenPassword=helpCheck.Password("Password","private-test-password");
        helpCheck.Validate=()=>{hiddenPassword.Clear();throw new System.Net.Http.HttpRequestException("SSL failed",new System.Security.Authentication.AuthenticationException("UntrustedRoot private-test-password"));};
        helpCheck.Loaded+=(_,_)=>{
            try {
                helpCheck.UpdateLayout();var helpButton=Buttons(helpCheck).Single(b=>b.Content as string=="?");
                if(!helpCheck.HelpTexts.Contains("A helpful server explanation."))throw new Exception("Help text missing");
                helpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var tip=(ToolTip)helpButton.ToolTip;
                if(!tip.IsOpen||((TextBlock)tip.Content).Text!="A helpful server explanation.")throw new Exception("Question-mark tooltip did not open");
                if(Application.Current.Windows.OfType<Window>().Any(w=>w.Title=="Help"))throw new Exception("Help opened a dialog");
                tip.IsOpen=false;
                Buttons(helpCheck).Single(b=>b.Content as string=="Submit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));helpCheck.UpdateLayout();
                var detail=Elements(helpCheck).OfType<ErrorDetails>().Single();if(detail.Visibility!=Visibility.Visible||detail.IsExpanded)throw new Exception("Error details are missing or not collapsed");
                detail.IsExpanded=true;helpCheck.UpdateLayout();
                var errorText=Elements(detail).OfType<TextBox>().Single().Text;
                if(!errorText.Contains("UntrustedRoot")||errorText.Contains("private-test-password"))throw new Exception("Nested cause lost or cleared password exposed");
                detail.Clear();if(detail.Visibility!=Visibility.Collapsed)throw new Exception("Old error details retained");
            }catch(Exception error){helpFailure=error;}
            helpCheck.Close();
        };
        helpCheck.ShowDialog();if(helpFailure!=null)throw helpFailure;
        var gate=new TaskCompletionSource<bool>();int submissions=0;
        var asyncDialog=new EditorDialog(this,"Async dialog check","Submit");
        asyncDialog.ValidateAsync=async()=> {submissions++;return await gate.Task;};
        Exception? failure=null;
        asyncDialog.Loaded+=async(_,_)=> {
            try {
                var submit=Buttons(asyncDialog).Single(b=>b.Content as string=="Submit");
                submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                asyncDialog.Close();
                if(!asyncDialog.IsVisible||submissions!=1||submit.IsEnabled)throw new Exception("Setup allowed duplicate submission or closed during writes.");
                gate.SetResult(false);await Task.Delay(50);
                if(!submit.IsEnabled)throw new Exception("Setup form did not recover after validation.");
            } catch(Exception e){failure=e;gate.TrySetResult(false);await Task.Delay(50);}
            asyncDialog.Close();
        };
        asyncDialog.ShowDialog();if(failure!=null)throw failure;
        var outer=new EditorDialog(this,"Setup preview");
        AddAdministratorSetup(outer,outer.Text("Address","https://pve.example.test:8006"),outer.Text("Token"),outer.Password("Secret"),outer.Check("Save",false));
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};
        bool captured=false;
        timer.Tick+=(_,_)=> {
            var wizard=Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>w.Title=="Automatic Proxmox access setup");
            if(wizard==null)return;
            timer.Stop();wizard.UpdateLayout();
            try {
                var bitmap=new RenderTargetBitmap((int)wizard.ActualWidth,(int)wizard.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(wizard);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file=File.Create(Path.Combine(AppContext.BaseDirectory,"setup-preview.png"));png.Save(file);captured=true;
            } catch(Exception e){failure=e;}
            wizard.Close();
        };
        outer.Loaded+=(_,_)=> {
            timer.Start();Buttons(outer).Single(b=>b.Content as string=="Set up access with administrator…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));outer.Close();
        };
        try {outer.ShowDialog();} finally {timer.Stop();}
        if(failure!=null)throw failure;
        if(!captured)throw new Exception("Setup dialog did not open.");
        IEnumerable<DependencyObject> Elements(DependencyObject parent) {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
                var child=VisualTreeHelper.GetChild(parent,i);yield return child;
                foreach(var nested in Elements(child))yield return nested;
            }
        }
        foreach(bool automatic in new[]{true,false}) {
            bool branchShown=false,managerAddStarted=false;
            var modeTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(200)};
            modeTimer.Tick+=(_,_)=> {
                var window=Application.Current.Windows.OfType<EditorDialog>().LastOrDefault();
                if(window==null)return;
                window.UpdateLayout();
                try {
                    if(window.Title=="Manage servers" && !managerAddStarted) {
                        managerAddStarted=true; Dispatcher.BeginInvoke(new Action(()=>{Buttons(window).Single(b=>b.Content as string=="Add server…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.Close();}));return;
                    }
                    if(window.Title=="Add server") {
                        var radio=Elements(window).OfType<RadioButton>().Single(r=>(r.Content as string)!.StartsWith(automatic?"Automatic":"Manual"));
                        var preview=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);preview.Render(window);
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(preview));using(var file=File.Create(Path.Combine(AppContext.BaseDirectory,"setup-choice-preview.png")))encoder.Save(file);
                        radio.IsChecked=true;Buttons(window).Single(b=>b.Content as string=="Continue").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));return;
                    }
                    if(window.Title!=(automatic?"Add server automatically":"Add server manually"))return;
                    modeTimer.Stop();
                    var tls=Elements(window).OfType<CheckBox>().Single(c=>c.Content as string=="Ignore HTTPS certificate errors (unsafe)");
                    if(tls.IsChecked==true)throw new Exception("Unsafe TLS default");
                    tls.IsChecked=true;window.UpdateLayout();
                    if(!Elements(window).OfType<TextBlock>().Any(t=>t.Text.Contains("could be intercepted")&&t.IsVisible))throw new Exception("TLS warning missing");
                    var addressBox=Elements(window).OfType<TextBox>().First(t=>t.IsEnabled&&!t.IsReadOnly);var savedText=addressBox.Text;
                    // Locate the actual address field by assigning text only after its known sibling label.
                    var labelsPanel=Elements(window).OfType<StackPanel>().First(p=>p.Children.OfType<StackPanel>().Any(row=>row.Children.OfType<TextBlock>().Any(t=>t.Text=="Proxmox address (HTTPS)")));
                    var labelRow=labelsPanel.Children.OfType<StackPanel>().Single(row=>row.Children.OfType<TextBlock>().Any(t=>t.Text=="Proxmox address (HTTPS)"));
                    addressBox=(TextBox)labelsPanel.Children[labelsPanel.Children.IndexOf(labelRow)+1];savedText=addressBox.Text;addressBox.Text="https://changed.example.test:8006";
                    if(tls.IsChecked==true)throw new Exception("TLS exception retained after changing server address");addressBox.Text=savedText;
                    var labels=string.Join("\n",Elements(window).OfType<TextBlock>().Select(t=>t.Text).Concat(window.HelpTexts));
                    if(labels.Contains("Token secret") == automatic)throw new Exception("Wrong fields shown for selected server setup mode.");
                    if(!labels.Contains("https://pve.example.org:8006"))throw new Exception("Address example missing.");
                    if(!automatic&&!labels.Contains("dalizo@pve!windows-client"))throw new Exception("Token ID example missing.");
                    var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                    var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
                    using(var file=File.Create(Path.Combine(AppContext.BaseDirectory,automatic?"automatic-server-preview.png":"manual-server-preview.png")))png.Save(file);
                    branchShown=true;window.Close();
                } catch(Exception e) {failure=e;modeTimer.Stop();window.Close();}
            };
            Activate();ShowSidebar(false);HideStatusNotification();OptionsToggle.IsChecked=true;UpdateLayout();
            if(!OptionsPopup.IsOpen || !ManageServersOption.IsVisible)throw new Exception("Options menu does not expose Manage servers.");
            modeTimer.Start();try {ManageServersOption.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));} finally {modeTimer.Stop();}
            if(OptionsPopup.IsOpen)throw new Exception("Options menu stayed open after opening server setup.");
            if(failure!=null)throw failure;
            if(!branchShown)throw new Exception("Server setup choice did not route to expected form.");
        }
        await Task.CompletedTask;
        var beforeServers=data;
        try {
            Refresh();
            if(ServerColumn.Visibility!=Visibility.Collapsed)throw new Exception("Server column visible with one server.");
            var extra=new ServerProfile(Guid.NewGuid(),"Second lab","https://second.example.test:8006","test@pve!client","",null);
            data=new(data.Version,data.Servers.Append(extra).ToList(),data.Vms);Refresh();
            if(ServerColumn.Visibility!=Visibility.Visible)throw new Exception("Server column hidden with multiple servers.");
            Activate();ShowSidebar(true);UpdateLayout();
            foreach(double width in new[]{540d,656d}) {
                VmList.Width=width-38;UpdateLayout();UpdateVmColumnWidths();UpdateLayout();
                foreach(var button in Elements(VmList).OfType<Button>().Where(b=>b.ToolTip as string is "Connect" or "Show console" or "Disconnect" or "VM properties")) {
                    var right=button.TransformToAncestor(VmList).Transform(new Point(button.ActualWidth,0)).X;
                    if(right>VmList.ActualWidth+0.5)throw new Exception("VM action button extends outside the list.");
                }
                if(VmList.Columns.Where(c=>c.Visibility==Visibility.Visible).Sum(c=>c.ActualWidth)>VmList.ActualWidth+0.5)
                    throw new Exception("VM columns exceed available width.");
            }
            VmList.Width=double.NaN;UpdateLayout();UpdateVmColumnWidths();
            bool managerSeen=false;
            var managerTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(200)};
            managerTimer.Tick+=(_,_)=> {
                var window=Application.Current.Windows.OfType<EditorDialog>().FirstOrDefault(w=>w.Title=="Manage servers");
                if(window==null)return;
                managerTimer.Stop();window.UpdateLayout();
                try {
                    Servers.SelectedItem=extra;
                    var actions=Buttons(window).Where(b=>b.Content as string is "Add server…" or "Edit server…" or "Remove server…" or "Repair permissions…").ToArray();
                    if(actions.Length!=4||actions.Any(b=>!b.IsEnabled))throw new Exception("Server manager actions missing or disabled.");
                    var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(AppContext.BaseDirectory,"manage-servers-preview.png")))png.Save(file);
                    managerSeen=true;
                }catch(Exception e){failure=e;}
                window.Close();
            };
            Activate();OptionsToggle.IsChecked=true;UpdateLayout();
            managerTimer.Start();try {ManageServersOption.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}finally{managerTimer.Stop();}
            if(failure!=null)throw failure;
            if(!managerSeen||Servers.Parent!=null)throw new Exception("Server manager did not open or release its list.");
        } finally {data=beforeServers;Refresh();}
        if(ServerColumn.Visibility!=Visibility.Collapsed)throw new Exception("Server column did not hide again after removal.");
    }
}

