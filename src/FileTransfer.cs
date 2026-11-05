// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace ProxmoxSpice;
public partial class MainWindow
{
    bool transferringFiles;
    void UpdateFileTransferButton()
    {
        if(SendFilesButton==null)return;
        string? reason=!HasDisplayedConsole||host==null?"Connect a SPICE console first.":targetVm?.FileTransferEnabled!=true?"Enable file sending in VM settings > Console.":!host.GuestAgentConnected?"Install or start the SPICE guest agent inside the VM.":transferringFiles?"A file transfer is already in progress.":busy?"Wait for the current operation.":null;
        SendFilesButton.IsEnabled=reason==null;
        SendFilesButton.ToolTip=reason??"Send regular files from this computer to the VM. The guest agent selects the destination. Folders are not supported.";
    }
    async void SendFilesClicked(object sender,RoutedEventArgs e)
    {
        ManagementToggle.IsChecked=false;
        if(targetVm is not SavedVm vm||host is not SpiceHost source||!HasDisplayedConsole)return;
        var picker=new Microsoft.Win32.OpenFileDialog {Title="Select files to send to "+vm.Name,Multiselect=true,CheckFileExists=true};
        if(picker.ShowDialog(this)==true)await ShowFileTransfer(vm,source,picker.FileNames);
    }
    async Task ShowFileTransfer(SavedVm vm,SpiceHost source,string[] paths)
    {
        if(transferringFiles||busy)return;
        try{
            if(!HasDisplayedConsole||host!=source||targetVm?.Id!=vm.Id||!vm.FileTransferEnabled||!source.GuestAgentConnected)throw new InvalidOperationException("File sending requires an active SPICE console, guest agent and the VM's file-transfer setting.");
            var files=SpiceWorker.ValidateTransferFiles(paths);
            transferringFiles=true;UpdateFileTransferButton();ManagementToggle.IsChecked=false;
            using var cancel=new CancellationTokenSource();
            var dialog=new Window {Owner=this,Title="Send files to "+vm.Name,Width=520,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,FontFamily=new System.Windows.Media.FontFamily("Segoe UI"),FontSize=13,Background=System.Windows.Media.Brushes.WhiteSmoke};
            dialog.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("/DALIZO-Native;component/DialogStyles.xaml",UriKind.Relative)});
            var panel=new StackPanel {Margin=new Thickness(22)};dialog.Content=panel;
            panel.Children.Add(new TextBlock {Text=vm.Name+" · VM "+vm.VmId,FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)});
            panel.Children.Add(new ListBox {ItemsSource=files.Select(Path.GetFileName).ToArray(),MaxHeight=180});
            var status=new TextBlock {Text="Send these files to the VM? The guest agent chooses the destination. Existing files are not explicitly overwritten by DALIZO.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,12)};panel.Children.Add(status);
            var bar=new ProgressBar {Minimum=0,Maximum=100,Height=8,Margin=new Thickness(0,0,0,18)};panel.Children.Add(bar);
            var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(actions);
            var close=new Button {Content="Cancel",Margin=new Thickness(0,0,10,0)};var send=new Button {Content="Send files",Style=(Style)dialog.FindResource("PrimaryDialogButton")};actions.Children.Add(close);actions.Children.Add(send);
            bool running=false,finished=false;
            void Cancel(){if(running){cancel.Cancel();close.IsEnabled=false;status.Text="Cancelling… Partial files may remain in the guest.";}else dialog.Close();}
            close.Click+=(_,_)=>Cancel();dialog.Closing+=(_,args)=>{if(running){args.Cancel=true;Cancel();}};
            send.Click+=async(_,_)=>{
                if(running||finished)return;running=true;send.IsEnabled=false;close.Content="Cancel transfer";bar.IsIndeterminate=true;status.Text="Sending…";
                try{
                    var progress=new Progress<(long Current,long Total)>(p=>{if(!running||cancel.IsCancellationRequested)return;bar.IsIndeterminate=p.Total<=0;if(p.Total>0)bar.Value=Math.Clamp(100.0*p.Current/p.Total,0,100);status.Text=$"{p.Current:N0} / {p.Total:N0} bytes sent";});
                    await source.SendFilesAsync(files,progress,cancel.Token);
                    status.Text="Files sent successfully. Check the Desktop or Downloads folder inside the VM.";bar.Value=100;
                }catch(Exception error){status.Text=cancel.IsCancellationRequested?"Transfer cancelled. Partial files may remain in the guest.":"Transfer failed: "+error.Message;}
                finally{running=false;finished=true;bar.IsIndeterminate=false;close.IsEnabled=true;close.Content="Close";}
            };
            dialog.ShowDialog();
            if(host==source&&HasDisplayedConsole)source.FocusConsole();
        }catch(Exception error){Status("File transfer: "+error.Message);}
        finally{transferringFiles=false;UpdateFileTransferButton();}
        await Task.CompletedTask;
    }
}
