using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Skia;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ProxyPartnerLite.Desktop;

return AppBuilder.Configure<ComparisonApp>().UseWin32().UseSkia()
    .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

internal sealed class ComparisonApp : Application
{
    private static readonly string[] Headers = ["节点", "当前", "原生延迟", "OpenAI API", "ChatGPT", "GitHub", "最近样本 (UTC)"];
    private const string Widths = "192,54,102,126,112,102,142";
    private readonly BaselineSource source = new();
    private Window? panel;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            var open = new NativeMenuItem("打开");
            var exit = new NativeMenuItem("退出");
            var menu = new NativeMenu();
            menu.Items.Add(open);
            menu.Items.Add(exit);
            var tray = new TrayIcon { ToolTipText = "ProxyPartner · Avalonia 对照", Menu = menu };
            tray.Clicked += (_, _) => ShowPanel();
            open.Click += (_, _) => ShowPanel();
            exit.Click += (_, _) =>
            {
                tray.Dispose();
                lifetime.Shutdown();
            };
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            ShowPanel();
            if (lifetime.Args is ["--peak-probe"])
                DispatcherTimer.RunOnce(() =>
                {
                    tray.Dispose();
                    lifetime.Shutdown();
                }, TimeSpan.FromSeconds(8));
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ShowPanel()
    {
        if (panel is not null)
        {
            panel.Activate();
            return;
        }
        var snapshot = source.GetSnapshot();
        var search = new TextBox { Watermark = "筛选节点", Width = 250 };
        var groups = new CheckBox { Content = "显示策略组" };
        var status = new TextBlock();
        var rows = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<DesktopRow>((row, _) =>
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Widths) };
                string[] text = [row!.Kind == NodeKind.Group ? $"[组] {row.Name}" : row.Name, row.IsCurrent ? "当前" : "—",
                    row.Native.DisplayText, row.OpenAi.DisplayText, row.ChatGpt.DisplayText,
                    row.GitHub.DisplayText, row.LastObservedUtc?.ToString("MM-dd HH:mm") ?? "未知"];
                for (int column = 0; column < text.Length; column++)
                {
                    var cell = new TextBlock { Text = text[column] };
                    Grid.SetColumn(cell, column);
                    grid.Children.Add(cell);
                }
                return grid;
            })
        };
        int sort = 0;
        bool descending = false;
        void Refresh()
        {
            var selected = (rows.SelectedItem as DesktopRow)?.Id;
            var query = snapshot.Rows.Where(row => (groups.IsChecked == true || row.Kind == NodeKind.Leaf) &&
                row.Name.Contains(search.Text?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));
            Func<DesktopRow, IComparable?> key = sort switch
            {
                1 => row => !row.IsCurrent,
                2 => row => row.Native.ElapsedMs,
                3 => row => row.OpenAi.ElapsedMs,
                4 => row => row.ChatGpt.ElapsedMs,
                5 => row => row.GitHub.ElapsedMs,
                6 => row => row.LastObservedUtc,
                _ => row => row.Name
            };
            var present = query.OrderBy(row => key(row) is null);
            var ordered = descending ? present.ThenByDescending(key) : present.ThenBy(key);
            var items = ordered.ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
            rows.ItemsSource = items;
            rows.SelectedItem = items.FirstOrDefault(row => row.Id == selected);
            status.Text = $"合成数据 · 桌面基线  |  未接入宿主  |  实际链路：未知  |  {items.Length} 行";
        }
        search.TextChanged += (_, _) => Refresh();
        groups.IsCheckedChanged += (_, _) => Refresh();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(Widths) };
        for (int i = 0; i < Headers.Length; i++)
        {
            int column = i;
            var button = new Button { Content = Headers[i], HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) =>
            {
                descending = sort == column && !descending;
                sort = column;
                Refresh();
            };
            Grid.SetColumn(button, column);
            header.Children.Add(button);
        }
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        tools.Children.Add(search);
        tools.Children.Add(groups);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), Margin = new Thickness(12) };
        Control[] controls = [status, tools, header, rows];
        for (int i = 0; i < controls.Length; i++)
        {
            Grid.SetRow(controls[i], i);
            layout.Children.Add(controls[i]);
        }
        panel = new Window { Title = "ProxyPartner · Avalonia 对照", Width = 900, Height = 500, Content = layout };
        panel.Closed += (_, _) => panel = null;
        Refresh();
        panel.Show();
    }
}
