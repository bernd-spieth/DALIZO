// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace ProxmoxSpice;
public partial class MainWindow
{

    bool sidebarVisible=true;
    Point? sidebarAnchor;
    bool sidebarButtonDismissed;

    void InitializeFloatingSidebar()
    {
        var openState=System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            System.Windows.Controls.Primitives.Popup.IsOpenProperty,typeof(System.Windows.Controls.Primitives.Popup));
        EventHandler synchronizeOpenState=(_, _) => {
            // Persist WPF's outside-click dismissal so layout/status updates
            // cannot immediately open the menu again.
            if(!SidebarOverlay.IsOpen && sidebarVisible) {
                var point=System.Windows.Input.Mouse.GetPosition(SidebarButton);
                sidebarButtonDismissed=System.Windows.Input.Mouse.LeftButton==System.Windows.Input.MouseButtonState.Pressed
                    && new Rect(SidebarButton.RenderSize).Contains(point);
                ShowSidebar(false);
            }
        };
        openState.AddValueChanged(SidebarOverlay,synchronizeOpenState);
        Closed+=(_,_)=>openState.RemoveValueChanged(SidebarOverlay,synchronizeOpenState);
        SidebarOverlay.PlacementTarget=WorkspaceGrid;
        SidebarOverlay.Placement=System.Windows.Controls.Primitives.PlacementMode.Custom;
        SidebarOverlay.CustomPopupPlacementCallback=(_, _, _) => new[] {
            new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(0,0),System.Windows.Controls.Primitives.PopupPrimaryAxis.None)
        };
        LocationChanged += (_, _) => RepositionSidebar();
        SizeChanged += (_, _) => RepositionSidebar();
        Loaded += (_, _) => { UpdateLayout(); RepositionSidebar(); };
        StateChanged += (_, _) => RepositionSidebar();
        WorkspaceGrid.LayoutUpdated += (_, _) => RepositionSidebar();
        Activated += (_, _) => RepositionSidebar();
        Deactivated += (_, _) => { SidebarOverlay.IsOpen=false; OptionsToggle.IsChecked=false; };
        Closed += (_, _) => SidebarOverlay.IsOpen=false;
    }
    void RepositionSidebar()
    {
        if(closing || !IsLoaded || !IsActive || WindowState==WindowState.Minimized || !sidebarVisible) {
            SidebarOverlay.IsOpen=false;sidebarAnchor=null;return;
        }
        if(WorkspaceGrid.ActualWidth<=0 || WorkspaceGrid.ActualHeight<=0)return;
        var anchor=WorkspaceGrid.PointToScreen(new Point(0,0));
        SidebarPanel.Width=Math.Min(656,Math.Max(0,WorkspaceGrid.ActualWidth-24));
        SidebarOverlay.IsOpen=true;
        if(sidebarAnchor==anchor)return;
        sidebarAnchor=anchor;
        var offset=SidebarOverlay.HorizontalOffset;
        SidebarOverlay.HorizontalOffset=offset+1;SidebarOverlay.HorizontalOffset=offset;
    }
    void ShowSidebar(bool visible)
    {
        if(visible) { sidebarButtonDismissed=false; HideStatusNotification(); }
        sidebarVisible=visible;
        if(!visible) { SidebarOverlay.IsOpen=false;sidebarAnchor=null; }
        else RepositionSidebar();

        SidebarButton.ToolTip=visible?"Hide VM list":"Show VM list";
    }
    void SetConsoleFocus(bool enabled) => ShowSidebar(!enabled);
    void ToggleSidebar(object sender,RoutedEventArgs e)
    {
        if(sidebarButtonDismissed) { sidebarButtonDismissed=false; return; }
        ShowSidebar(!sidebarVisible);

    }
    void ConsoleAvailabilityChanged(bool available)
    {
        CtrlAltDeleteButton.IsEnabled=available;
        UpdateConnectButtons();
        if(!available)SetConsoleFocus(false);
        else if(!smokeMode)ShowSidebar(false);
    }
    async Task CheckConsoleLayout()
    {
        var oldLeft=Left;var oldTop=Top;
        Width=900;Height=540;
        var area=SystemParameters.WorkArea;
        Left=area.Left+Math.Max(0,(area.Width-Width)/3);
        Top=area.Top+Math.Max(0,(area.Height-Height)/3);
        Activate();ShowSidebar(true);await Task.Delay(200);UpdateLayout();
        void CheckAnchor() {
            var expected=WorkspaceGrid.PointToScreen(new Point(12,12));
            var actual=SidebarPanel.PointToScreen(new Point(0,0));
            if(!SidebarOverlay.IsOpen || Math.Abs(expected.X-actual.X)>3 || Math.Abs(expected.Y-actual.Y)>3)
                throw new InvalidOperationException($"Sidebar outside window anchor: expected {expected}, actual {actual}.");
        }
        CheckAnchor();
        SidebarOverlay.IsOpen=false;
        await Task.Delay(100);UpdateLayout();RepositionSidebar();
        if(sidebarVisible || SidebarOverlay.IsOpen)throw new InvalidOperationException("Dismissed sidebar reopened on layout refresh.");
        ShowSidebar(true);UpdateLayout();CheckAnchor();
        Left+=20;Top+=15;await Task.Delay(200);UpdateLayout();CheckAnchor();
        Left=oldLeft;Top=oldTop;
        if(SelectedVm is {} selected)SetTarget(selected);
        SetConsoleFocus(false);ShowSidebar(true);Width=1280;Height=840;await Task.Delay(150);UpdateLayout();
        var child=ConsoleArea.Child;
        double normalWidth=ConsoleArea.ActualWidth,normalHeight=ConsoleArea.ActualHeight;
        void Capture(string name) {
            var bitmap=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(this);
            if(SidebarOverlay.IsOpen && SidebarPanel.ActualWidth>0) {
                SidebarPanel.UpdateLayout();
                var panel=new RenderTargetBitmap((int)SidebarPanel.ActualWidth,(int)SidebarPanel.ActualHeight,96,96,PixelFormats.Pbgra32);panel.Render(SidebarPanel);
                var composed=new DrawingVisual();using(var dc=composed.RenderOpen()) {
                    dc.DrawImage(bitmap,new Rect(0,0,ActualWidth,ActualHeight));
                    var pos=WorkspaceGrid.TransformToAncestor(this).Transform(new Point(12,12));
                    dc.DrawImage(panel,new Rect(pos,new Size(SidebarPanel.ActualWidth,SidebarPanel.ActualHeight)));
                }
                bitmap=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(composed);
            }
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(AppContext.BaseDirectory,name));png.Save(stream);
        }
        Capture("compact-overview.png");
        ShowSidebar(false);UpdateLayout();
        if(Math.Abs(ConsoleArea.ActualWidth-normalWidth)>0.1||Math.Abs(ConsoleArea.ActualHeight-normalHeight)>0.1)
            throw new InvalidOperationException("Overview sidebar changed console dimensions.");
        ShowSidebar(true);SetConsoleFocus(true);UpdateLayout();
        double focusWidth=ConsoleArea.ActualWidth,focusHeight=ConsoleArea.ActualHeight;
        if(Math.Abs(focusWidth-normalWidth)>1||Math.Abs(focusHeight-normalHeight)>0.1||ConsoleArea.Child!=child||SidebarOverlay.IsOpen)
            throw new InvalidOperationException("Console focus layout failed or replaced viewer.");
        Capture("compact-focus.png");
        for(int i=0;i<4;i++) {
            ToggleSidebar(this,new RoutedEventArgs());UpdateLayout();
            if(Math.Abs(ConsoleArea.ActualWidth-focusWidth)>0.1||Math.Abs(ConsoleArea.ActualHeight-focusHeight)>0.1||ConsoleArea.Child!=child)
                throw new InvalidOperationException("Floating sidebar changed the console viewport or focus mode.");
        }
        ShowSidebar(true);UpdateLayout();Capture("floating-sidebar.png");ShowSidebar(false);
        Width=900;Height=540;await Task.Delay(150);UpdateLayout();
        if(ConsoleArea.ActualWidth<850||ConsoleArea.ActualHeight<410)throw new InvalidOperationException("Small window focus layout clipped.");
        SetConsoleFocus(false);UpdateLayout();
        if(!SidebarOverlay.IsOpen||ConsoleArea.Child!=child)
            throw new InvalidOperationException("Sidebar restoration failed.");
        Width=1280;Height=840;await Task.Delay(150);UpdateLayout();
        ManagementToggle.IsChecked=true;await Task.Delay(150);
        if(!StartButton.IsVisible||!VmPropertiesButton.IsVisible)throw new InvalidOperationException("VM action popup is inaccessible.");
        ManagementToggle.IsChecked=false;
        foreach(var power in new[]{StartButton,ShutdownButton,RebootButton,StopButton,DisconnectButton})
            if(!power.IsVisible || power.ToolTip is not System.Windows.Controls.ToolTip || power.Content is not System.Windows.Shapes.Path)
                throw new Exception("Toolbar power action missing its icon or explanatory tooltip.");
        ShowSidebar(false);Activate();UpdateLayout();
        var notificationSize=ConsoleArea.RenderSize;
        ShowStatusNotification();await Task.Delay(100);UpdateLayout();
        if(!StatusPopup.IsOpen || ConsoleArea.RenderSize!=notificationSize)throw new Exception("Floating status resized the console or did not open.");
        await Task.Delay(5200);UpdateLayout();
        if(StatusPopup.IsOpen || ConsoleArea.RenderSize!=notificationSize)throw new Exception("Status notification did not hide without resizing.");
        ShowStatusClicked(this,new RoutedEventArgs());
        if(!StatusPopup.IsOpen)throw new Exception("Latest status could not be reopened.");
        HideStatusNotification();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"layout-result.txt"),$"PASS: overview console {normalWidth:0}x{normalHeight:0}; focus {focusWidth:0}x{focusHeight:0}; 900x540 minimum window, floating sidebar in overview and focus without viewport resizing, unchanged viewer instance, VM action popup, top VM actions, full-height console and no bottom toolbar. Synthetic UI only.");
    }
}










