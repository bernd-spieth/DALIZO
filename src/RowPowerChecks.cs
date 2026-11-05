// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
namespace ProxmoxSpice;
public partial class MainWindow
{
    async Task CheckRowPower()
    {
        var factory=apiFactory;var oldTarget=targetVm;
        var a=data.Vms.First();var b=data.Vms.Last();int starts=0;bool running=false;
        try {
            runtime[a.Id]=new(new("running","",0),DateTimeOffset.UtcNow,null);
            runtime[b.Id]=new(new("stopped","",0),DateTimeOffset.UtcNow,null);Filter();
            var row=VmList.Items.OfType<VmRow>().Single(v=>v.Id==b.Id);
            var context=BuildVmContext(row,null);
            var powerItems=context.Items.OfType<MenuItem>().Take(3).ToArray();
            void CheckMenu(bool start,bool shutdown){if(!powerItems.Select(i=>i.IsEnabled).SequenceEqual(new[]{start,shutdown,shutdown}))throw new Exception("Context power action state failed.");}
            if(!powerItems.Select(i=>i.Header).SequenceEqual(new[]{"Start","Shut down…","Restart…"}))throw new Exception("Power menu entries missing.");
            CheckMenu(true,false);
            if(!row.CanStart||row.CanShutdown)throw new Exception("Stopped row action state failed.");
            runtime[b.Id]=new(new("stopped","",0),DateTimeOffset.UtcNow.AddSeconds(-30),null);UpdateConnectButtons();
            CheckMenu(false,false);if(row.CanStart||row.CanShutdown)throw new Exception("Stale row permits power action.");
            runtime[b.Id]=new(new("stopped","",0),DateTimeOffset.UtcNow,null);UpdateConnectButtons();
            busy=true;UpdateConnectButtons();if(row.CanStart||row.CanShutdown)throw new Exception("Busy row permits power action.");busy=false;UpdateConnectButtons();
            apiFactory=(server,key)=>{var api=new ProxmoxApi(server.Address,new SmokeHttp(req=>{
                var path=req.RequestUri!.AbsolutePath;
                if(req.Method==HttpMethod.Post){if(!path.EndsWith($"/qemu/{b.VmId}/status/start"))throw new Exception("Wrong VM/action.");starts++;running=true;SetTarget(null);return "{\"data\":\"UPID:node:test:\"}";}
                if(path.Contains("/tasks/"))return "{\"data\":{\"status\":\"stopped\",\"exitstatus\":\"OK\"}}";
                if(path.EndsWith("/status/current"))return "{\"data\":{\"status\":\""+(running?"running":"stopped")+"\"}}";
                if(path.EndsWith("/config"))return "{\"data\":{\"vga\":\"std\"}}";
                return "{\"data\":[{\"type\":\"qemu\",\"node\":\"node\",\"vmid\":"+a.VmId+",\"name\":\"First\",\"status\":\"running\"},{\"type\":\"qemu\",\"node\":\"node\",\"vmid\":"+b.VmId+",\"name\":\"Second\",\"status\":\""+(running?"running":"stopped")+"\"}]}";
            }));api.UseToken(server.TokenId,key);return api;};
            SetTarget(a);ShowSidebar(true);UpdateLayout();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            IEnumerable<DependencyObject> Children(DependencyObject p){for(int i=0;i<VisualTreeHelper.GetChildrenCount(p);i++){var c=VisualTreeHelper.GetChild(p,i);yield return c;foreach(var n in Children(c))yield return n;}}
            var button=Children(VmList).OfType<Button>().Single(x=>Equals(x.Tag,"start")&&x.DataContext is VmRow r&&r.Id==b.Id);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for(int i=0;i<100&&(starts==0||busy);i++)await Task.Delay(30);
            if(starts!=1||busy||runtime[b.Id].State?.Status!="running")throw new Exception("Row start failed or targeted selected VM instead of clicked row.");
            UpdateConnectButtons();CheckMenu(false,true);if(row.CanStart||!row.CanShutdown)throw new Exception("Started VM row actions failed.");
            runtime[b.Id]=new(new("stopped","",0),DateTimeOffset.UtcNow,null);UpdateConnectButtons();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            if(starts!=1)throw new Exception("Server-side running recheck failed to prevent duplicate start.");
            if(VmList.Columns[0]!=NameColumn||VmList.Columns[1]!=ActionsColumn||(string)VmList.Columns[^1].Header!="VM ID"||(string)VmList.Columns[^2].Header!="Console")throw new Exception("Column order failed.");
            UpdateConnectButtons();UpdateLayout();
            bool confirmation=false;
            var pending=Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{
                var dialog=Application.Current.Windows.OfType<EditorDialog>().SingleOrDefault(w=>w.Title=="Confirm VM action");
                if(dialog!=null){confirmation=Children(dialog).OfType<TextBlock>().Any(t=>t.Text.Contains("VM “"+b.Name+"”")&&t.Text.Contains("ID "+b.VmId));dialog.DialogResult=false;}
            }));
            var shutdown=powerItems[2];
            shutdown.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));pending.Abort();
            if(!confirmation||starts!=1)throw new Exception("Row shutdown confirmation target/cancel failed.");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"row-power-result.txt"),"PASS: rendered row Start click sends exactly one request to that row's VM despite a different prior selection; stopped/running/stale/busy gating; server state recheck prevents duplicate start; context menu Start/Shut down/Restart follows stopped/running/stale states; context Restart confirmation identifies clicked VM and cancel sends no request; actions beside Name and Console/VM ID last. Synthetic HTTP only; no real VM started.");
        } finally {apiFactory=factory;busy=false;SetTarget(oldTarget);Filter();UpdateRuntimeHeader();}
    }
}

