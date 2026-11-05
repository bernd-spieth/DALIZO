// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
namespace ProxmoxSpice;

internal sealed class EditorDialog : Window
{
    readonly StackPanel fields = new() { Margin = new Thickness(22) };
    StackPanel activeFields;
    TabControl? tabs;
    Panel? helpAnchor;
    readonly List<string> helpTexts=new();
    internal IReadOnlyList<string> HelpTexts=>helpTexts;
    readonly ErrorDetails errorDetails=new();
    readonly TextBlock error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    public Func<bool>? Validate { get; set; }
    public Func<Task<bool>>? ValidateAsync { get; set; }
    public EditorDialog(Window owner, string title, string acceptText="Apply", string cancelText="Cancel", bool defaultAccept=true)
    {
        activeFields = fields;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri("/DALIZO;component/DialogStyles.xaml",UriKind.Relative) });
        Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(241,245,249)); Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(35,53,77));
        Owner = owner; Title = title; Width = 490; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); FontSize = 13;
        var root = new DockPanel(); Content = root;
        var bottom = new StackPanel { Margin = new Thickness(22, 0, 22, 20) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom); bottom.Children.Add(error);bottom.Children.Add(errorDetails);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; bottom.Children.Add(buttons);
        var cancel = new Button { Content = cancelText, IsCancel = true, IsDefault=!defaultAccept, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 10, 0) }; buttons.Children.Add(cancel);
        var save = new Button { Content = acceptText, IsDefault = defaultAccept, Padding = new Thickness(16, 7, 16, 7) }; save.Style=(Style)FindResource("PrimaryDialogButton"); buttons.Children.Add(save);
        bool submitting=false;
        Closing+=(_,e)=> { if(submitting)e.Cancel=true; };
        save.Click += async (_, _) => {
            if(submitting)return;
            var secrets=Passwords(fields).Select(p=>p.Password).Where(p=>p.Length>0).ToArray();
            errorDetails.Clear();
            submitting=true;save.IsEnabled=cancel.IsEnabled=fields.IsEnabled=false;
            error.Text=ValidateAsync!=null ? "Working… Please keep this window open until setup finishes." : "";
            try {
                bool accepted=ValidateAsync!=null ? await ValidateAsync() : Validate?.Invoke()!=false;
                submitting=false;
                if(accepted)DialogResult=true;
            } catch(Exception e) {error.Text="The action could not be completed. Open the error details below.";errorDetails.Set(e,secrets);}
            finally {submitting=false;save.IsEnabled=cancel.IsEnabled=fields.IsEnabled=true;}
        };
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        root.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
    public void Tab(string title)
    {
        if (tabs == null) { tabs = new TabControl(); fields.Children.Add(tabs); Width = 620; }
        helpAnchor=null;activeFields = new StackPanel { Margin = new Thickness(12) };
        tabs.Items.Add(new TabItem { Header = title, Content = new ScrollViewer { Content = activeFields, Height = Math.Min(420, SystemParameters.WorkArea.Height - 220), VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
    }
    public TextBox Text(string caption, string value = "") { Label(caption); var box = new TextBox { Text = value, Padding = new Thickness(8, 6, 8, 6) }; activeFields.Children.Add(box); return box; }
    public PasswordBox Password(string caption, string value = "") { Label(caption); var box = new PasswordBox { Password = value, Padding = new Thickness(8, 6, 8, 6) }; activeFields.Children.Add(box); return box; }
    public CheckBox Check(string caption, bool selected) { var box = new CheckBox { Content = caption, IsChecked = selected, VerticalAlignment=VerticalAlignment.Center }; var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,14,0,4)};row.Children.Add(box);activeFields.Children.Add(row);helpAnchor=row; return box; }
    public ComboBox Choice(string caption, object[] items, object? selected) { Label(caption); var box = new ComboBox { ItemsSource = items, SelectedItem = selected ?? items.FirstOrDefault(), DisplayMemberPath = "Name", Padding = new Thickness(6) };
        if(caption=="Console") {
            // Display label only: retain the stored Serial terminal value.
            box.DisplayMemberPath="";
            var text=new FrameworkElementFactory(typeof(TextBlock));text.Name="ConsoleName";
            text.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding());
            var template=new DataTemplate {VisualTree=text};
            template.Triggers.Add(new DataTrigger {Binding=new System.Windows.Data.Binding(),Value="Serial terminal",Setters={new Setter(TextBlock.TextProperty,"Serial","ConsoleName")}});
            box.ItemTemplate=template;
        }
        activeFields.Children.Add(box); return box; }
    public void Hint(string text) => activeFields.Children.Add(new TextBlock { Text=text,TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.SlateGray,Margin=new Thickness(0,4,0,0) });
    public void Note(string text) => activeFields.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.SlateGray, Margin = new Thickness(0, 12, 0, 0) });
    public void Details(string text) => activeFields.Children.Add(new Expander {
        Header="Technical details", IsExpanded=false, Margin=new Thickness(0,12,0,0),
        Content=new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8)}
    });
    public void AddField(UIElement field) {
        if(field is RadioButton option){
            var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=option.Margin};option.Margin=new Thickness(0);option.VerticalAlignment=VerticalAlignment.Center;
            row.Children.Add(option);activeFields.Children.Add(row);helpAnchor=row;
        }else activeFields.Children.Add(field);
    }
    public ListBox List(string caption, object[] items) { Label(caption); var list = new ListBox { ItemsSource = items, DisplayMemberPath = "Caption", SelectionMode = SelectionMode.Extended, Height = 280 }; activeFields.Children.Add(list); return list; }
    void Label(string text) {var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,5)};row.Children.Add(new TextBlock{Text=text,VerticalAlignment=VerticalAlignment.Center});activeFields.Children.Add(row);helpAnchor=row;}
    static IEnumerable<PasswordBox> Passwords(DependencyObject root){if(root is PasswordBox box)yield return box;foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())foreach(var nested in Passwords(child))yield return nested;}
    public void Help(string text){
        helpTexts.Add(text);
        var anchor=helpAnchor;if(anchor==null){anchor=new StackPanel{Orientation=Orientation.Horizontal};activeFields.Children.Add(anchor);helpAnchor=anchor;}
        var help=anchor.Children.OfType<Button>().FirstOrDefault(b=>b.Tag is string && b.Content as string=="?");
        if(help!=null){help.Tag=(string)help.Tag+"\n\n"+text;((TextBlock)((ToolTip)help.ToolTip).Content).Text=(string)help.Tag;return;}
        help=new Button{Content="?",Tag=text,Width=24,Height=24,MinHeight=0,VerticalAlignment=VerticalAlignment.Center,Padding=new Thickness(0),Margin=new Thickness(6,0,0,0),ToolTip=new ToolTip{Content=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,MaxWidth=360}}};
        System.Windows.Automation.AutomationProperties.SetName(help,"Show help");ToolTipService.SetShowDuration(help,60000);
        var tip=(ToolTip)help.ToolTip;tip.PlacementTarget=help;tip.Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom;
        help.Click+=(_,_)=>tip.IsOpen=true;
        help.GotKeyboardFocus+=(_,_)=>tip.IsOpen=true;
        help.LostKeyboardFocus+=(_,_)=>tip.IsOpen=false;
        help.MouseLeave+=(_,_)=>tip.IsOpen=false;
        Closed+=(_,_)=>tip.IsOpen=false;
        anchor.Children.Add(help);
    }
    public static bool Confirm(Window owner,string message,string title)
    {
        var dialog=new EditorDialog(owner,title,"Yes","No",false);dialog.Note(message);
        return dialog.ShowDialog()==true;
    }
}




