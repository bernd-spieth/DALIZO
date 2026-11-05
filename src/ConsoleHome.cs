// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
namespace ProxmoxSpice;
public partial class MainWindow
{
    readonly List<Guid> recentConsoles=new();
    void TouchConsole(Guid id) { recentConsoles.Remove(id);recentConsoles.Add(id); }
    void CloseConsoleFromUser(Guid id)
    {
        bool active=targetVm?.Id==id;
        CloseConsole(id);
        if(!active)return;
        var next=recentConsoles.AsEnumerable().Reverse()
            .Where(key=>consoles.TryGetValue(key,out var session)&&session.Open)
            .Select(key=>data.Vms.FirstOrDefault(v=>v.Id==key)).FirstOrDefault(vm=>vm!=null);
        SetTarget(next);
        refreshingRows=true;
        try { VmList.SelectedItem=next==null ? null : VmList.Items.OfType<VmRow>().FirstOrDefault(r=>r.Id==next.Id); }
        finally { refreshingRows=false; }
        if(next==null)ShowConsoleHome();
        UpdateRuntimeHeader();
        Status(next==null ? "Console closed. Choose a VM from the VM menu." : "Console closed. Returned to "+next.Name+".");
    }
    void ShowConsoleHome()
    {
        var panel=new StackPanel {HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        panel.Children.Add(new TextBlock {Text="Ready for your VM",FontSize=24,HorizontalAlignment=HorizontalAlignment.Center});
        panel.Children.Add(new TextBlock {Text="Open the VM menu to select a console.",Margin=new Thickness(0,12,0,16)});
        var button=new Button {Content="Open VM menu",HorizontalAlignment=HorizontalAlignment.Center,Padding=new Thickness(18,9,18,9)};
        button.Click+=(_,_)=>ShowSidebar(true);panel.Children.Add(button);ConsoleArea.Child=panel;
    }
}
