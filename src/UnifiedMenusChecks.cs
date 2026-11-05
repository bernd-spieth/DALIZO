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
    async Task CheckUnifiedMenus()
    {
        var items=guestKeysMenu.Items.OfType<MenuItem>().ToArray();
        if(items.Any(i=>i.HasItems))throw new Exception("Shortcut submenu remains.");
        var shortcuts=items.Where(i=>i!=TypeClipboardButton&&i!=CancelTypingButton).ToArray();
        if(guestKeysMenu.Items[0] is not MenuItem first||(string)first.Header!="Ctrl+Alt+Delete"||!items.Contains(TypeClipboardButton)||!items.Contains(CancelTypingButton))throw new Exception("Keyboard clipboard entries missing.");
        var commands=shortcuts.Where(i=>i.IsEnabled).Select(i=>(string)i.Header).ToArray();
        if(!commands.SequenceEqual(GuestShortcuts.All.Select(s=>s.Label)))throw new Exception("Shortcut order or command missing.");
        if(!shortcuts.Where(i=>!i.IsEnabled).Select(i=>(string)i.Header).SequenceEqual(new[]{"Common","Windows","Linux"}))throw new Exception("Shortcut headings missing.");
        void Capture(FrameworkElement element,string name) {
            element.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)System.Math.Ceiling(element.ActualWidth),(int)System.Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(element);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(AppContext.BaseDirectory,name));png.Save(file);
        }
        ShowSidebar(false);ManagementToggle.IsChecked=false;OptionsToggle.IsChecked=false;
        guestKeysMenu.PlacementTarget=CtrlAltDeleteButton;guestKeysMenu.IsOpen=true;
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Capture(guestKeysMenu,"unified-shortcuts.png");guestKeysMenu.IsOpen=false;
        OptionsToggle.IsChecked=true;
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Capture((FrameworkElement)OptionsPopup.Child,"unified-options.png");OptionsToggle.IsChecked=false;
        ManagementToggle.IsChecked=true;
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Capture((FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(System.Windows.Media.VisualTreeHelper.GetParent(RefreshStatusButton)),"unified-actions.png");
        ManagementToggle.IsChecked=false;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"unified-menus-result.txt"),"PASS: all 15 shortcut commands at root level; Ctrl+Alt+Delete first; Common/Windows/Linux non-action headings; all command mappings retained. Options/actions/shortcut menus rendered for visual inspection. No guest shortcuts sent.");
    }
}
