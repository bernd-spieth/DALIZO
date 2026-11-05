// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ProxmoxSpice;
public partial class MainWindow
{
    async Task CheckRowActionBindings(Guid selected)
    {
        var identityProbe=new VmRow(Guid.NewGuid(),"Identity probe",999,"Test","Running","SPICE");
        var trackedRows=new HashSet<VmRow> {identityProbe};
        identityProbe.SetActions(true,true);
        identityProbe.SetSessionState("Console open");
        identityProbe.PropertyChanged+=(_,_)=>{};
        if(!trackedRows.Contains(identityProbe))throw new Exception("VM row identity changed after state/binding updates.");
        IEnumerable<Button> Buttons(DependencyObject parent) {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
                var child=VisualTreeHelper.GetChild(parent,i);
                if(child is Button button)yield return button;
                foreach(var nested in Buttons(child))yield return nested;
            }
        }
        Activate(); ShowSidebar(true);
        for(int pass=0;pass<20;pass++) {
            foreach(var item in VmList.Items.OfType<VmRow>().ToArray()) {
                VmList.SelectedItem=item;
                item.SetSessionState("Console open");
                item.SetActions(false,true);
                item.SetSessionState("");
                item.SetActions(true,false);
            }
            busy=true; UpdateConnectButtons();
            Filter(selected); VmList.UpdateLayout();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            Filter(selected); VmList.UpdateLayout();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            busy=false; UpdateConnectButtons();
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            var buttons=Buttons(VmList).Where(b=>b.ToolTip as string=="Connect").ToArray();
            if(buttons.Length==0)throw new Exception("No rendered Connect buttons found.");
            foreach(var button in buttons) {
                if(button.DataContext is not VmRow row || !VmList.Items.Cast<object>().Any(item=>ReferenceEquals(item,row)))
                    throw new Exception("Connect button retained an obsolete VM row after refresh.");
                if(!row.CanConnect || !button.IsEnabled)throw new Exception($"Rendered Connect disabled after refresh {pass}.");
            }
        }
    }
}
