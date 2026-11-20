using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FolderScope.Core;
using Microsoft.Win32;

namespace FolderScope.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--watch-responsiveness")
        {
            if (args.Length == 3 && int.TryParse(args[1], out var processId) && long.TryParse(args[2], out var startTicks))
                ResponsivenessWatchdog.Watch(processId, startTicks);
            return;
        }
        void RecordLifecycle(string stage)
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderScope");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"lifecycle-{Environment.ProcessId}.log"), $"{DateTime.UtcNow:O} {stage}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        RecordLifecycle("starting");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => RecordLifecycle("unhandled-" + e.ExceptionObject.GetType().Name);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RecordLifecycle("process-exit-code-" + Environment.ExitCode);
        try { ResponsivenessWatchdog.Start(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { MessageBox.Show("无响应保护未能启动：" + ex.Message, "Flash-X"); return; }
        var application = new Application();
        var window = new ScanWindow(args.FirstOrDefault() == "--snapshot" ? null : args.FirstOrDefault(), args.Length == 2 && args[0] == "--snapshot" ? args[1] : null);
        void ProtectionFailed(string reason) => window.Dispatcher.BeginInvoke(new Action(() => window.ReportProtectionFailure(reason)));
        ResponsivenessWatchdog.ProtectionFailed += ProtectionFailed;
        if (ResponsivenessWatchdog.FailureReason is string failure) window.ReportProtectionFailure(failure);
        window.Closed += (_, _) => RecordLifecycle("window-closed");
        application.Exit += (_, e) => RecordLifecycle("application-exit-code-" + e.ApplicationExitCode);
        try { application.Run(window); }
        finally { ResponsivenessWatchdog.ProtectionFailed -= ProtectionFailed; }
    }
}

public sealed class ScanWindow : Window
{
    private readonly TextBlock _protectionWarning = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkRed, Visibility = Visibility.Collapsed };
    public void ReportProtectionFailure(string reason)
    {
        _protectionWarning.Text = "无响应保护失效，已请求停止当前操作，请重新启动程序：" + reason;
        _protectionWarning.Visibility = Visibility.Visible;
        _cancellation?.Cancel();
    }
    private string _scanTarget = ComputerScanner.SelectionName;
    private readonly ComboBox _scanScope = new();
    private readonly TextBox _path = new() { Height = 32, MinWidth = 90, Margin = new Thickness(6, 0, 0, 0), IsReadOnly = true, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "当前扫描目标；选择盘符或目录后自动更新" };
    private readonly TextBlock _summary = new() { FontSize = 13, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 7, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Height = 19, FontSize = 11, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 9, 0, 0) };
    private readonly TextBlock _selectedPath = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 8) };
    private const int FolderPageSize = 10000;
    private const int TreePageSize = 2000;
    private readonly StackPanel _folderPager = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button _folderPrevious = MakeButton("列表上一页");
    private readonly Button _folderNext = MakeButton("列表下一页");
    private readonly TextBlock _folderPageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private int _folderPageOffset;
    private CancellationTokenSource? _browseCancellation;
    private long _browseRequest;
    private int _browseOffset;
    private readonly StackPanel _filePager = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly Button _filePrevious = MakeButton("文件上一页");
    private readonly Button _fileNext = MakeButton("文件下一页");
    private readonly TextBlock _filePageLabel = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 650 };
    private readonly TreeView _tree = new();
    private readonly TextBlock _historyLabel = new() { Text = "暂无扫描记录", TextTrimming = TextTrimming.CharacterEllipsis,
        FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _historySelector = new() { Height = 26, MinHeight = 26, Padding = new Thickness(8, 2, 8, 2),
        HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private DateTime? _historyFinishedUtc;
    private ScanHistoryOverview? _historyPreview;
    private int _previewNodeId;
    private bool _restoringHistoryView;
    private (string Path, long Length, DateTime WriteUtc)? _loadedSnapshot;
    private bool _selectingTreeContext;
    private readonly DataGrid _table = new() { AutoGenerateColumns = false, EnableRowVirtualization = true, EnableColumnVirtualization = true, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly DataGridTextColumn _allocatedColumn = new() { Header = "占用空间（去重累计）", MinWidth = 150, Binding = new Binding("Allocated"), SortMemberPath = "Node.TotalAllocatedBytes", Width = DataGridLength.Auto };
    private readonly Button _scan = MakeButton("扫描");
    private readonly Button _cancel = MakeButton("停止");
    private readonly Button _export = MakeButton("导出报告");
    private ScanReport? _report;
    private Dictionary<int, List<ScanNode>> _children = [];
    private IReadOnlyDictionary<int, int> _directDirectoryCounts = new Dictionary<int, int>();
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private int _memoryBudgetGiB = 8;
    private bool _retainFileNodes = true;
    private bool _measureAllocatedSpace;
    private readonly MenuItem _diskMode = new() { Header = "文件明细落盘（下次扫描）", IsCheckable = true, IsChecked = true };
    private string? _fileDetailsPath;
    private int _detailPageOffset;
    private int _detailPageFolder = -1;
    private bool _detailHasMore;
    private ScanReport? _fileDetailsReport;
    private readonly MenuItem _compactMode = new() { Header = "低内存目录汇总（下次扫描，不保留文件明细）", IsCheckable = true };
    private int _currentFolderId;
    private readonly SizeChart _chart = new();
    private long _chartRequest;
    private CancellationTokenSource? _chartCancellation;
    private readonly Dictionary<int, IReadOnlyList<ScanNode>> _chartSummaries = [];
    private readonly AnalysisChat _analysisChat = new();
    private readonly Grid _details = new();
    private readonly RowDefinition _chartRow = new() { Height = new GridLength(220), MinHeight = 140 };
    private readonly RowDefinition _chartSplitterRow = new() { Height = new GridLength(6) };
    private Func<ScanNode, bool> _sizeFilter = _ => true, _baseFilter = _ => true;
    private string _quickSearchText = "";
    private Func<ScanNode, bool> _nameFilter
    {
        get
        {
            var size = _sizeFilter; var name = _baseFilter; var query = _quickSearchText;
            return node => size(node) && name(node) && (query.Length == 0 || node.Path.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        set => _baseFilter = value;
    }
    private readonly TextBox _searchInput = new() { Height = 32, Width = 224, Margin = new Thickness(8, 0, 0, 0), ToolTip = "按名称或路径筛选整份扫描结果" };
    private StackPanel? _resultFilterPanel;
    private readonly TextBox _resultMinimumMiB = new() { Height = 32, Width = 62, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 0, 7, 0), ToolTip = "筛选整份扫描中严格大于此 MiB 值的项目；留空取消大小筛选" };
    private string _filterDescription = "";
    private bool _filterExclude;
    private string _nameMasks = "";
    private NodeFilterOptions? _advancedFilter;
    private readonly ProgressBar _progress = new() { Height = 8, Visibility = Visibility.Hidden, Margin = new Thickness(0, 4, 0, 0) };

    public ScanWindow(string? initialPath, string? snapshotPath = null)
    {
        Title = "Flash-X v2026.11.16.8 · 磁盘空间分析"; Width = 1120; Height = 730; MinWidth = 800; MinHeight = 530;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Resources.MergedDictionaries.Add(InterfaceTheme.Create());
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(244, 246, 249));
        var outer = new DockPanel { Margin = new Thickness(14) };
        var menuRow = new DockPanel { Height = 26, Margin = new Thickness(0, 0, 0, 5) };
        var settingsButton = new Button
        {
            Content = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("F0 M8,0 L10,0 L10.6,2.3 L12,2.9 L14.1,1.7 L15.5,3.1 L14.3,5.2 L14.9,6.6 L17.2,7.2 L17.2,9.2 L14.9,9.8 L14.3,11.2 L15.5,13.3 L14.1,14.7 L12,13.5 L10.6,14.1 L10,16.4 L8,16.4 L7.4,14.1 L6,13.5 L3.9,14.7 L2.5,13.3 L3.7,11.2 L3.1,9.8 L0.8,9.2 L0.8,7.2 L3.1,6.6 L3.7,5.2 L2.5,3.1 L3.9,1.7 L6,2.9 L7.4,2.3 Z M11.7,8.2 A2.7,2.7 0 1 0 6.3,8.2 A2.7,2.7 0 1 0 11.7,8.2 Z"),
                Fill = Brushes.SlateGray, Width = 16, Height = 16, Stretch = Stretch.Uniform
            },
            Width = 26,
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "设置 AI 模型和分析提示词"
        };
        System.Windows.Automation.AutomationProperties.SetName(settingsButton, "模型设置");
        settingsButton.Click += (_, _) => { ModelSettings.Edit(this); _analysisChat.RefreshModels(); };
        DockPanel.SetDock(settingsButton, Dock.Right);
        menuRow.Children.Add(settingsButton);
        var menu = BuildMenu();
        menu.Margin = new Thickness(0);
        menu.VerticalAlignment = VerticalAlignment.Center;
        foreach (MenuItem item in menu.Items) item.Padding = new Thickness(8, 2, 8, 2);
        menuRow.Children.Add(menu);
        DockPanel.SetDock(menuRow, Dock.Top);
        outer.Children.Add(menuRow);
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); top.Children.Add(_protectionWarning);
        var drives = _scanScope;
        drives.Height = 32; drives.Width = 130; drives.Margin = new Thickness(0, 0, 2, 0);
        drives.VerticalContentAlignment = VerticalAlignment.Center;
        drives.Items.Add("整个电脑");
        var driveNames = DriveInfo.GetDrives().Select(d => d.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var driveName in driveNames) drives.Items.Add(driveName);
        drives.SelectedIndex = 0;
        _path.Text = ComputerScanner.SelectionName;
        void SetTargetPath(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) target = ComputerScanner.SelectionName;
            if (string.Equals(target, ComputerScanner.SelectionName, StringComparison.OrdinalIgnoreCase))
            {
                if (drives.SelectedIndex != 0) drives.SelectedIndex = 0;
                _scanTarget = _path.Text = ComputerScanner.SelectionName;
                return;
            }
            var matchingDrive = driveNames.FirstOrDefault(name => target.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (matchingDrive is not null && !string.Equals(drives.SelectedItem as string, matchingDrive, StringComparison.OrdinalIgnoreCase))
                drives.SelectedItem = matchingDrive;
            _scanTarget = _path.Text = target;
        }
        drives.SelectionChanged += (_, _) =>
        {
            if (drives.SelectedItem is string selected && selected != ComputerScanner.SelectionName) _scanTarget = _path.Text = selected;
            else _scanTarget = _path.Text = ComputerScanner.SelectionName;
        };
        var scanBar = new DockPanel { Height = 28, Margin = new Thickness(0, 0, 0, 5) };
        void AddRight(FrameworkElement item) { DockPanel.SetDock(item, Dock.Right); scanBar.Children.Add(item); }
        DockPanel.SetDock(drives, Dock.Left); scanBar.Children.Add(drives);
        var browse = MakeButton("选目录");
        browse.Click += (_, _) =>
        {
            var selected = drives.SelectedItem as string;
            var picker = new OpenFolderDialog { Title = "选择扫描目录", InitialDirectory = selected is null or "整个电脑" ? null : selected };
            if (picker.ShowDialog(this) == true) SetTargetPath(picker.FolderName);
        };
        DockPanel.SetDock(browse, Dock.Left); scanBar.Children.Add(browse);
        _scan.Background = new SolidColorBrush(Color.FromRgb(19, 109, 82)); _scan.Foreground = Brushes.White;
        AddRight(_export); AddRight(_cancel); AddRight(_scan);
        AddRight(new TextBlock { Text = "MiB", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 2, 0) });
        AddRight(_detailMinimum);
        AddRight(new TextBlock { Text = "快速估算 >", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
        _export.ToolTip = "导出 AI 分析摘要（包含最大目录、最大文件及异常说明），或保存 JSON/CSV/XML 扫描统计。";
        scanBar.Children.Add(_path);
        top.Children.Add(scanBar);
        var search = new StackPanel { Orientation = Orientation.Horizontal };
        search.Children.Add(new TextBlock { Text = "名称", VerticalAlignment = VerticalAlignment.Center });
        search.Children.Add(_searchInput);
        search.Children.Add(new TextBlock { Text = "大于", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        search.Children.Add(_resultMinimumMiB);
        search.Children.Add(new TextBlock { Text = "MiB", VerticalAlignment = VerticalAlignment.Center });
        var searchButton = MakeButton("筛选");
        async Task ApplyResultFilters()
        {
            if (_report is null || _busy) return;
            _quickSearchText = _searchInput.Text.Trim();
            if (!string.IsNullOrWhiteSpace(_resultMinimumMiB.Text))
            {
                if (!long.TryParse(_resultMinimumMiB.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minimum)
                    || minimum < 0 || minimum > long.MaxValue / 1048576)
                { _status.Text = "大小请输入非负整数 MiB。"; return; }
                var minimumBytes = minimum * 1048576;
                _sizeFilter = node => (NodeSizeStatus.Get(node) is NodeSizeState.Known or NodeSizeState.Partial) && node.TotalBytes > minimumBytes;
            }
            else _sizeFilter = _ => true;
            if (_quickSearchText.Length == 0 && string.IsNullOrWhiteSpace(_resultMinimumMiB.Text))
            { SelectFolder(_currentFolderId); _status.Text = "已清除结果筛选"; return; }
            await ShowReport("全局大小筛选");
        }
        search.Children.Add(searchButton);
        searchButton.Content = "筛选全部结果";
        searchButton.Click += async (_, _) => await ApplyResultFilters();
        _searchInput.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await ApplyResultFilters(); } };
        _resultMinimumMiB.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await ApplyResultFilters(); } };
        var filterDelay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        filterDelay.Tick += async (_, _) => { if (_busy) return; filterDelay.Stop(); await ApplyResultFilters(); };
        _searchInput.TextChanged += (_, _) => { filterDelay.Stop(); filterDelay.Start(); };
        _resultMinimumMiB.TextChanged += (_, _) => { filterDelay.Stop(); filterDelay.Start(); };
        Closed += (_, _) => filterDelay.Stop();
        search.IsEnabled = _report is not null;
        _resultFilterPanel = search;
        var filterRow = new DockPanel();
        var historyButton = _historySelector;
        var historyContent = new DockPanel();
        var historyArrow = new TextBlock { Text = "⌄", FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(historyArrow, Dock.Right);
        historyContent.Children.Add(historyArrow);
        historyContent.Children.Add(_historyLabel);
        historyButton.Content = historyContent;
        historyButton.ToolTip = "载入最近三次扫描记录";
        System.Windows.Automation.AutomationProperties.SetName(historyButton, "最近三次扫描");
        historyButton.SetResourceReference(StyleProperty, "HistoryMenuButton");
        historyButton.Click += (_, _) =>
        {
            if (_busy) { _status.Text = "当前操作完成后可切换扫描记录。"; return; }
            var menu = new ContextMenu { PlacementTarget = historyButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            try
            {
                foreach (var snapshot in ScanHistory.Recent())
                {
                    var record = ScanHistoryEntry.Read(snapshot);
                    var entry = new MenuItem { Header = record.Label, ToolTip = record.Target, IsCheckable = true, IsChecked = record.FinishedUtc == _historyFinishedUtc };
                    entry.Click += async (_, _) => await LoadReport(record.Path);
                    menu.Items.Add(entry);
                }
                if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "暂无扫描记录", IsEnabled = false });
                historyButton.ContextMenu = menu;
                menu.IsOpen = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { _status.Text = "扫描记录未读取：" + ex.Message; }
        };
        filterRow.Children.Add(search);
        filterRow.Height = 28;
        top.Children.Add(filterRow);
        // Compact only the main command rows; preserve settings and chat control sizes.
        foreach (var control in scanBar.Children.OfType<Control>().Concat(search.Children.OfType<Control>()))
        {
            control.Height = control.MinHeight = 28;
            control.Padding = new Thickness(8, 2, 8, 2);
        }
        top.Children.Add(_summary);
        outer.Children.Add(top);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(_status);
        _status.Cursor = System.Windows.Input.Cursors.Hand;
        _status.MouseLeftButtonUp += (_, _) => ShowIssues();
        _status.ToolTip = "点击查看当前扫描的问题明细";
        footer.Children.Add(_progress);
        outer.Children.Add(footer);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 130 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 320 });
        VirtualizingStackPanel.SetIsVirtualizing(_tree, true);
        VirtualizingStackPanel.SetVirtualizationMode(_tree, VirtualizationMode.Recycling);
        var directoryPane = new DockPanel();
        DockPanel.SetDock(historyButton, Dock.Top);
        directoryPane.Children.Add(historyButton);
        directoryPane.Children.Add(_tree);
        _tree.BorderThickness = new Thickness(0);
        body.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(224, 231, 235)),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Child = directoryPane
        });
        var splitter = new GridSplitter { Width = 8, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        System.Windows.Automation.AutomationProperties.SetName(splitter, "左右窗格分隔条");
        Grid.SetColumn(splitter, 1);
        body.Children.Add(splitter);

        var right = new Grid { Margin = new Thickness(6, 0, 0, 0) };
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 100 });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 125 });
        Grid.SetColumn(right, 2);
        body.Children.Add(right);
        var chatSplitter = new GridSplitter { Height = 8, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Color.FromRgb(232, 238, 241)),
            ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        System.Windows.Automation.AutomationProperties.SetName(chatSplitter, "列表和聊天分隔条");
        Grid.SetRow(chatSplitter, 1);
        right.Children.Add(chatSplitter);
        var chatSurface = new Border { Child = _analysisChat, Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(217, 227, 234)), BorderThickness = new Thickness(1) };
        Grid.SetRow(chatSurface, 2);
        right.Children.Add(chatSurface);
        var details = _details;
        right.Children.Add(details);
        ScrollViewer.SetVerticalScrollBarVisibility(_table, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_table, ScrollBarVisibility.Auto);
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition());
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(_chartSplitterRow); details.RowDefinitions.Add(_chartRow);
        _summary.SetBinding(ToolTipProperty, new Binding("Text") { Source = _summary });
        var selectionPanel = new StackPanel();
        selectionPanel.Children.Add(_selectedPath);
        _folderPrevious.Click += (_, _) => SelectFolder(_currentFolderId, Math.Max(0, _folderPageOffset - FolderPageSize));
        _folderNext.Click += (_, _) => SelectFolder(_currentFolderId, _folderPageOffset + FolderPageSize);
        _folderPager.Children.Add(_folderPrevious); _folderPager.Children.Add(_folderNext); _folderPager.Children.Add(_folderPageLabel);
        selectionPanel.Children.Add(_folderPager);
        _filePrevious.Click += (_, _) => BeginFolderFiles(Math.Max(0, _browseOffset - 1000));
        _fileNext.Click += (_, _) => BeginFolderFiles(_browseOffset + 1000);
        _filePager.Children.Add(_filePrevious); _filePager.Children.Add(_fileNext); _filePager.Children.Add(_filePageLabel);
        selectionPanel.Children.Add(_filePager);
        details.Children.Add(selectionPanel);
        var chartPanel = new DockPanel(); Grid.SetRow(chartPanel, 4);
        var chartModes = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(chartModes, Dock.Top);
        foreach (var mode in new[] { "饼图", "柱图" })
        {
            var button = MakeButton(mode);
            button.Click += (_, _) => { _chart.Bars = mode == "柱图"; _chart.InvalidateVisual(); };
            chartModes.Children.Add(button);
        }
        chartPanel.Children.Add(chartModes); chartPanel.Children.Add(_chart);
        var chartSplitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetRow(chartSplitter, 3); details.Children.Add(chartSplitter);
        details.Children.Add(chartPanel); Grid.SetRow(_table, 1); details.Children.Add(_table);
        _chart.Selected += node => { if (node.IsDirectory) SelectFolder(node.Id); else _status.Text = node.Path; };
        outer.Children.Add(body); Content = outer;
        foreach (var (title, member) in new[] { ("名称", "Name"), ("大小", "Size") })
            _table.Columns.Add(new DataGridTextColumn { Header = title, MinWidth = title == "名称" ? 120 : 80, Binding = new Binding(member), SortMemberPath = member == "Size" ? "SizeValue" : member, Width = title == "名称" ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto });
        _table.Columns.Add(_allocatedColumn);
        var percentFactory = new FrameworkElementFactory(typeof(Grid));
        var percentBar = new FrameworkElementFactory(typeof(ProgressBar));
        percentBar.SetValue(ProgressBar.MinimumProperty, 0d);
        percentBar.SetValue(ProgressBar.MaximumProperty, 100d);
        percentBar.SetValue(ProgressBar.HeightProperty, 16d);
        percentBar.SetValue(ProgressBar.ForegroundProperty, new SolidColorBrush(Color.FromRgb(173, 224, 195)));
        percentBar.SetValue(ProgressBar.BackgroundProperty, new SolidColorBrush(Color.FromRgb(237, 247, 241)));
        percentBar.SetValue(ProgressBar.BorderThicknessProperty, new Thickness(0));
        percentBar.SetBinding(ProgressBar.ValueProperty, new Binding("PercentValue") { Mode = BindingMode.OneWay, TargetNullValue = 0d });
        percentBar.SetBinding(UIElement.VisibilityProperty, new Binding("HasPercent") { Converter = new BooleanToVisibilityConverter() });
        percentFactory.AppendChild(percentBar);
        var percentText = new FrameworkElementFactory(typeof(TextBlock));
        percentText.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        percentText.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        percentText.SetValue(TextBlock.MarginProperty, new Thickness(4, 0, 0, 0));
        percentText.SetBinding(TextBlock.TextProperty, new Binding("Percent"));
        percentFactory.AppendChild(percentText);
        var percentTemplate = new DataTemplate { VisualTree = percentFactory };
        _table.Columns.Add(new DataGridTemplateColumn { Header = "百分比", MinWidth = 115, SortMemberPath = "PercentValue", CellTemplate = percentTemplate, Width = DataGridLength.Auto });
        foreach (var (title, member) in new[] { ("文件数", "Files"), ("直接目录数", "DirectDirectories"), ("累计子目录数", "Directories"), ("创建时间", "Created"), ("修改时间", "Modified"), ("访问时间", "Accessed"), ("属性", "Attributes"), ("状态", "State"), ("完整路径", "Path"), ("路径长度", "PathLength"), ("名称长度", "NameLength") })
            _table.Columns.Add(new DataGridTextColumn { Header = title, MinWidth = title is "完整路径" ? 250 : title.EndsWith("时间") ? 150 : title == "名称" ? 120 : 80, Binding = new Binding(member), SortMemberPath = member switch { "Created" => "Node.CreatedUtc", "Modified" => "Node.ModifiedUtc", "Accessed" => "Node.AccessedUtc", _ => member }, Width = title == "名称" ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto });
        _scan.Click += async (_, _) => await StartScan();
        _cancel.Click += (_, _) =>
        {
            if (_cancellation is not { IsCancellationRequested: false } active) return;
            _cancel.IsEnabled = false;
            _status.Text = "已请求停止，等待当前操作结束…";
            _status.ToolTip = _status.Text;
            active.Cancel();
        };
        _export.Click += async (_, _) => await ExportReport();
        _tree.RequestBringIntoView += (_, e) => { if (_selectingTreeContext) e.Handled = true; };
        _tree.SelectedItemChanged += (_, e) =>
        {
            if (_restoringHistoryView || e.NewValue is not TreeViewItem { Tag: int id }) return;
            if (_historyPreview is { } preview && e.NewValue is TreeViewItem { DataContext: ValueTuple<ScanNode, long> selection })
                ShowHistoryPreviewRows(preview, selection.Item1, selection.Item2);
            else SelectFolder(id);
        };
        VirtualizingStackPanel.SetVirtualizationMode(_table, VirtualizationMode.Recycling);
        _table.MouseDoubleClick += (_, _) =>
        {
            if (_table.SelectedItem is NodeRow row && row.Node.IsDirectory) SelectFolder(row.Node.Id);
        };
        var directoryMenu = new ContextMenu();
        var openDirectory = new MenuItem { Header = "在资源管理器中打开" };
        directoryMenu.Items.Add(openDirectory); _table.ContextMenu = directoryMenu;
        ScanNode? contextDirectory = null;
        _table.ContextMenuOpening += (_, e) =>
        {
            var row = ItemsControl.ContainerFromElement(_table, e.OriginalSource as DependencyObject) as DataGridRow;
            var candidate = row?.Item as NodeRow;
            contextDirectory = candidate?.Node;
            if (contextDirectory is null) { e.Handled = true; return; }
            row!.IsSelected = true;
        };
        openDirectory.Click += (_, _) => { if (contextDirectory is not null) OpenInExplorer(contextDirectory.Path); };
        AddAnalysisActions(directoryMenu, () => contextDirectory);
        Closing += (_, _) => { _analysisChat.Cancel(); CancelFolderBrowse(); _cancellation?.Cancel(); _chartCancellation?.Cancel(); ++_chartRequest; };
        _cancel.IsEnabled = _export.IsEnabled = false;
        _summary.Text = "选择目录后开始扫描";
        try
        {
            var settings = DesktopSettings.Load();
            Width = settings.Width; Height = settings.Height;
            _memoryBudgetGiB = settings.MemoryBudgetGiB;
            _retainFileNodes = settings.RetainFileNodes; _compactMode.IsChecked = !_retainFileNodes;
            SetTargetPath(settings.LastPath); _chart.Bars = settings.Bars;
            _nameFilter = NameFilter.Create(settings.NameMasks, settings.Exclude);
            _filterDescription = settings.NameMasks; _filterExclude = settings.Exclude;
            _nameMasks = settings.NameMasks;
            if (settings.AdvancedFilter is { } advanced)
            {
                _nameFilter = NodeFilter.Create(advanced); _advancedFilter = advanced;
                _filterDescription = DescribeFilter(advanced);
            }
            // Show folder proportions in the table by default; chart remains optional in View.
            SetChartVisible(false);
        }
        // DesktopSettings.Load reports invalid window size or memory budget as InvalidDataException,
        // which is not an IOException; without it a corrupt settings file would abort startup.
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { _status.Text = "设置未恢复：" + ex.Message; }
        if (!string.IsNullOrWhiteSpace(snapshotPath)) Loaded += async (_, _) => await LoadReport(snapshotPath);
        if (!string.IsNullOrWhiteSpace(initialPath)) { SetTargetPath(initialPath); Loaded += async (_, _) => await StartScan(); }
        else if (string.IsNullOrWhiteSpace(snapshotPath)) Loaded += async (_, _) =>
        {
            try
            {
                var latest = ScanHistory.Recent().FirstOrDefault();
                if (latest is not null) await LoadReport(latest, automatic: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _status.Text = "上次结果未恢复：" + ex.Message; }
        };
    }

    private readonly TextBox _detailMinimum = new() { Height = 32, Text = "1", Width = 54, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "估算只累计大于此 MiB 的文件；默认 1 MiB。输入 0 则包含全部文件。" };
    private async Task StartScan()
    {
        if (_busy) return;
        long minimumMiB = 0;
        if (!long.TryParse(_detailMinimum.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minimumMiB)
            || minimumMiB < 0 || minimumMiB > long.MaxValue / 1048576)
        { _status.Text = "请输入快速估算门槛的非负整数 MiB；默认 1。"; return; }
        if (ResponsivenessWatchdog.FailureReason is string protectionFailure) { ReportProtectionFailure(protectionFailure); return; }
        CancelFolderBrowse(); string? diskPath = null;
        _busy = true; _scan.IsEnabled = false; _export.IsEnabled = false; _cancel.IsEnabled = true;
        _cancellation = new CancellationTokenSource();
        _summary.Text = _report is null ? "扫描中…" : "扫描中…（下方暂保留上次结果）"; _status.Text = "正在读取目录和文件元数据";
        SetProgress(true);
        var scanCancellation = _cancellation;
        using var livePreview = new LiveScanPreview(_tree);
        using var progress = new ThrottledScanProgress(p =>
        {
            if (_cancellation != scanCancellation || scanCancellation.IsCancellationRequested) return;
            _status.Text = $"已发现 {p.Entries:N0} 项：{p.Path}";
            _status.ToolTip = p.Path + "\n点击查看已完成扫描的问题明细";
        });
        try
        {
            var path = _scanTarget;
            var wholeComputer = string.Equals(path.Trim(), ComputerScanner.SelectionName, StringComparison.OrdinalIgnoreCase);
            if (_diskMode.IsChecked)
            {
                var userCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderScope", "scan-cache");
                var temporaryCache = Path.Combine(Path.GetTempPath(), "FolderScope", "scan-cache");
                var candidates = wholeComputer
                    ? new[] { userCache, temporaryCache, Path.Combine(AppContext.BaseDirectory, "scan-cache") }
                    : new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "scan-cache"),
                    userCache,
                    temporaryCache
                };
                string? cacheFailure = null;
                foreach (var candidate in candidates)
                {
                    if (!wholeComputer && !ScanCacheLocation.IsOutsideScan(path, candidate)) continue;
                    try
                    {
                        Directory.CreateDirectory(candidate);
                        diskPath = Path.Combine(candidate, "scan-" + Guid.NewGuid().ToString("N") + ".jsonl");
                        // Verify write access now; the detail store will create its own fresh file.
                        using (new FileStream(diskPath + ".probe", FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cacheFailure = ex.Message; diskPath = null; }
                }
                if (diskPath is null) throw new IOException("未找到扫描目录之外的可写缓存位置。" + cacheFailure);
            }
            var token = _cancellation.Token;
            var retainFileNodes = diskPath is null && _retainFileNodes;
            var measureAllocatedSpace = _measureAllocatedSpace;
            var memoryBudgetBytes = (long)_memoryBudgetGiB * 1024 * 1024 * 1024;
            var (report, index, detailStatus) = await Task.Run(() =>
            {
                using var detailStore = diskPath is null ? null : new FileDetailStore(diskPath);
                var report = wholeComputer
                    ? ComputerScanner.Scan(token, progress, maximumManagedBytes: memoryBudgetBytes, retainFileNodes: retainFileNodes,
                        measureAllocatedSpace: measureAllocatedSpace, fileDetails: detailStore, minimumFileBytes: minimumMiB * 1048576,
                        excludedCacheDirectory: diskPath is null ? null : Path.GetDirectoryName(diskPath), preview: livePreview.Publish)
                    : new DirectoryScanner().Scan(path, token, progress, maximumManagedBytes: memoryBudgetBytes,
                        retainFileNodes: retainFileNodes, measureAllocatedSpace: measureAllocatedSpace, fileDetails: detailStore,
                        minimumFileBytes: minimumMiB * 1048576, preview: livePreview.Publish);
                Dispatcher.Invoke(() =>
                {
                    progress.Dispose();
                    if (_cancellation == scanCancellation)
                    {
                        _status.Text = report.Cancelled ? "已停止读取，正在整理部分结果…" : "文件读取结束，正在整理目录和列表…";
                        _status.ToolTip = _status.Text;
                    }
                });
                detailStore?.Finish(writeIndex: !token.IsCancellationRequested);
                if (detailStore?.IndexWriteError is string indexError)
                    report.AddIssue(new ScanIssue(diskPath!, "file-detail-index", "目录索引保存失败，文件明细将顺序读取：" + indexError));
                var index = DirectoryIndex.Create(report, sortChildren: !report.Cancelled, sortCancellationToken: token);
                var detailStatus = "";
                if (report.FileDetailsPath is string detailPath)
                {
                    try
                    {
                        FileDetailStore.ValidateAssociation(detailPath, report.FileDetailsStoreId);
                        detailStatus = report.FileDetailsStoreId is null ? " · 旧版明细：关联未经验证" : " · 文件明细关联已核对";
                    }
                    // InvalidDataException is a sibling of IOException, not a subclass: a truncated or
                    // replaced detail file fails association validation and must degrade to a status
                    // message rather than escape the async click handler and terminate the process.
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                    { detailStatus = " · 文件明细不可用：" + ex.Message; }
                }
                if (report.Cancelled || token.IsCancellationRequested)
                    detailStatus += " · 已跳过自动保存，可手动导出当前结果";
                else try
                {
                    ScanHistory.Save(report, index, cancellationToken: token);
                    detailStatus += " · 已自动保存（保留最近三次）";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { detailStatus += " · 自动保存已停止，可手动导出当前结果"; }
                // History writes go through the same validating readers; a corrupt 4 MiB overview
                // or detail index surfaces as InvalidDataException, which must not abort the Task.Run body.
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                { detailStatus += " · 自动保存未完成：" + ex.Message; }
                return (report, index, detailStatus);
            });
            _chartCancellation?.Cancel(); ++_chartRequest; _chartSummaries.Clear(); CancelFolderBrowse(); _detailPageFolder = -1; _detailPageOffset = 0; _detailHasMore = false; _report = report; SetHistoryRecord(report.RootPath, report.FinishedUtc); if (_resultFilterPanel is not null) _resultFilterPanel.IsEnabled = true; _allocatedColumn.Visibility = report.MeasuresAllocatedSpace ? Visibility.Visible : Visibility.Collapsed; _children = index.Children;
            if (report.RootPath == ComputerScanner.SelectionName && _children.TryGetValue(0, out var drivesInOrder))
                drivesInOrder.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path)); _directDirectoryCounts = index.DirectDirectories;
            _fileDetailsPath = report.FileDetailsPath; _fileDetailsReport = report.FileDetailsPath is null ? null : report;
            _loadedSnapshot = null;
            livePreview.Dispose();
            _tree.Items.Clear(); var root = TreeNode(_report.Root); _tree.Items.Add(root); root.IsExpanded = true; root.IsSelected = true;
            _summary.Text = (_report.Complete ? "" : "部分结果 · ") + (minimumMiB == 0 ? "完整统计（包含全部文件）" : $"快速估算（仅累计 > {minimumMiB} MiB 文件）") + $" · {DisplayNodeSize(_report.Root, _report.MinimumFileBytes)}  ·  {_report.Root.TotalFiles:N0} 个文件  ·  {_report.Root.TotalDirectories:N0} 个子目录";
            if (_report.EstimateExcludedFiles > 0) _summary.Text += $" · 排除小文件 {_report.EstimateExcludedFiles:N0} 个";
            _status.Text = $"{(_report.Cancelled ? "已取消，结果不完整" : _report.Complete ? "扫描完成" : "扫描完成，部分项未读取")}" +
                $" · 问题 {_report.Issues.Count + _report.SuppressedIssueCount} · 跳过链接 {_report.Nodes.Count(n => n.LinkSkipped) + _report.UnlistedSkippedLinks} · {(_report.FinishedUtc - _report.StartedUtc).TotalSeconds:F2} 秒";
            if (_report.MeasuresAllocatedSpace) _summary.Text += " · 占用 " + (_report.Root.TotalAllocatedBytes is long allocatedBytes ? FormatBytes(allocatedBytes) : "未知（部分未读取）");
            var limitIssue = _report.Issues.FirstOrDefault(i => i.Operation is "node-limit" or "memory-limit");
            if (limitIssue is not null) _status.Text = (limitIssue.Operation == "node-limit" ? "达到节点数量上限，扫描提前停止。请启用扫描菜单中的文件明细落盘后重新扫描。" : "达到内存保护上限，扫描提前停止；目录数据仍占用内存，当前仅为部分结果。") + " · " + limitIssue.Message;
            // Full issue paths belong in the tooltip and issue dialog, not a clipped second status line.
            _status.Text += " · " + ScanDiagnostics.Summarize(_report) + detailStatus;
            if (index.SortingCancelled) _status.Text += " · 已停止后续目录排序，扫描统计保留";
            _status.ToolTip = _status.Text + "\n点击查看问题明细。" + (_report.Issues.Count > 0 ? "\n" + _report.Issues[0].Path + ": " + _report.Issues[0].Message : "");
            _export.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            _summary.Text = _report is null ? "扫描未完成" : "本次扫描未完成 · 下方仍为上次结果";
            _status.Text = ex.Message + (_report is null ? "" : " · 上次结果目录：" + _report.RootPath);
        }
        finally
        { _busy = false; _scan.IsEnabled = true; _export.IsEnabled = _report != null; _cancel.IsEnabled = false; _cancellation.Dispose(); _cancellation = null; SetProgress(false); BeginFolderFiles(0); }
    }
    private static TextBlock FolderHeader(TreeViewItem item, string text)
    {
        var header = new TextBlock { Text = text };
        header.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            item.Focus();
            item.IsSelected = true;
            if (item.HasItems) item.IsExpanded = true;
        };
        return header;
    }

    private TreeViewItem TreeNode(ScanNode node)
    {
        var item = new TreeViewItem { Tag = node.Id };
        var header = FolderHeader(item, $"{node.Name}   {DisplayNodeSize(node, _report?.MinimumFileBytes ?? 0)}");
        item.Header = header;
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "在资源管理器中打开" };
        open.Click += (_, _) => OpenInExplorer(node.Path);
        menu.Items.Add(open);
        // Attach the menu to the header only. A parent TreeViewItem also contains its descendants.
        header.ContextMenu = menu;
        AddAnalysisActions(menu, () => node);
        header.ContextMenuOpening += (_, _) =>
        {
            if (item.IsSelected) return;
            _selectingTreeContext = true;
            try { item.IsSelected = true; }
            finally { _selectingTreeContext = false; }
        };
        if (_children.GetValueOrDefault(node.Id)?.Any(n => n.IsDirectory) == true)
        {
            item.Items.Add("…");
            item.Expanded += (_, e) =>
            {
                if (item.Items.Count == 1 && item.Items[0] is string)
                    PopulateTreePage(item, node.Id, 0);
                e.Handled = true;
            };
        }
        return item;
    }
    private void PopulateTreePage(TreeViewItem item, int parentId, int offset)
    {
        var page = (_children.GetValueOrDefault(parentId) ?? []).Where(n => n.IsDirectory).Skip(offset).Take(TreePageSize + 1).ToList();
        var hasMore = page.Count > TreePageSize;
        item.Items.Clear();
        if (offset > 0)
        {
            var previous = MakeButton("上一页子目录");
            previous.Click += (_, e) => { PopulateTreePage(item, parentId, Math.Max(0, offset - TreePageSize)); e.Handled = true; };
            item.Items.Add(previous);
        }
        foreach (var child in page.Take(TreePageSize)) item.Items.Add(TreeNode(child));
        if (hasMore)
        {
            var next = MakeButton("下一页子目录");
            next.Click += (_, e) => { PopulateTreePage(item, parentId, offset + TreePageSize); e.Handled = true; };
            item.Items.Add(next);
        }
    }
    private void SelectFolder(int id, int pageOffset = 0)
    {
        try { SelectFolderCore(id, pageOffset); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            CancelFolderBrowse();
            _chartCancellation?.Cancel();
            ++_chartRequest;
            _table.ItemsSource = null;
            _chart.SetSummary([]);
            _folderPrevious.IsEnabled = false;
            _folderNext.IsEnabled = false;
            _folderPager.Visibility = Visibility.Collapsed;
            _status.Text = "名称筛选耗时过长，本次列表未读取；请清除或简化筛选条件后重试。";
        }
    }
    private void SelectFolderCore(int id, int pageOffset = 0)
    {
        CancelFolderBrowse(); _detailPageFolder = -1; _detailPageOffset = 0; _detailHasMore = false;
        if (_report is null) return;
        _currentFolderId = id; _folderPageOffset = pageOffset;
        var reportForChat = _report;
        var node = reportForChat.Nodes[id];
        _analysisChat.SetTarget(reportForChat, node.Id, node.Path, () => BuildAnalysisMaterial(reportForChat, node));
        _selectedPath.Text = node.Path + " · 列表每页10000项，目录树每页2000项（可继续翻页，统计保留全部；排序仅当前页）";
        if (_report.RootPath == ComputerScanner.SelectionName) _selectedPath.Text += " · 百分比以所在盘已读取的逻辑大小为基准";
        if (_report.MinimumFileBytes > 0) _selectedPath.Text += $" · 快速估算门槛 > {_report.MinimumFileBytes / 1048576} MiB";
        if (!_report.RetainsFileNodes) _selectedPath.Text += ReferenceEquals(_fileDetailsReport, _report) && _fileDetailsPath is not null ? " · 文件明细按需读取（每页1000项）" : " · 目录汇总模式：未保留单文件明细";
        if (node.Incomplete || node.LinkSkipped) _selectedPath.Text += " · " + ScanDiagnostics.DescribeNode(node) + " · 图表及百分比仅基于已读取数据，非完整目录占比";
        _selectedPath.ToolTip = _selectedPath.Text;
        _selectedPath.Text = node.Path == "shell:MyComputerFolder" ? "整个电脑" : node.Path;
        var page = (_children.GetValueOrDefault(id) ?? []).Where(_nameFilter).Skip(pageOffset).Take(FolderPageSize + 1).ToList();
        var hasMore = page.Count > FolderPageSize;
        _table.ItemsSource = page.Take(FolderPageSize).Select(n => new NodeRow(n, PercentBase(_report, n, node.TotalBytes), _directDirectoryCounts.GetValueOrDefault(n.Id), _report.MeasuresAllocatedSpace, _report.MinimumFileBytes)).ToList();
        _folderPrevious.IsEnabled = pageOffset > 0; _folderNext.IsEnabled = hasMore;
        _folderPageLabel.Text = $"第{pageOffset / FolderPageSize + 1}页 · {Math.Min(page.Count, FolderPageSize):N0}项";
        _folderPager.Visibility = pageOffset > 0 || hasMore ? Visibility.Visible : Visibility.Collapsed;
        _chartCancellation?.Cancel();
        var request = ++_chartRequest;
        if (_chartSummaries.TryGetValue(id, out var summary)) _chart.SetSummary(summary);
        else
        {
            _chart.SetSummary([]);
            _chartCancellation = new CancellationTokenSource();
            IEnumerable<ScanNode> chartNodes = _children.GetValueOrDefault(id) ?? [];
            if (!_report.RetainsFileNodes && node.UnlistedFileBytes > 0)
            {
                var directFiles = new ScanNode { Id = -2, Path = "", Name = $"直属文件汇总（{node.UnlistedFileCount}项）", TotalBytes = node.UnlistedFileBytes };
                chartNodes = chartNodes.Append(directFiles);
            }
            _ = PrepareChart(_report, id, request, chartNodes, _chartCancellation);
        }
        if (_filterDescription.Length > 0) _selectedPath.Text += " · 列表筛选：" + _filterDescription + "（统计和图表为全部项）";
        BeginFolderFiles(0);
    }
    private void CancelFolderBrowse()
    {
        ++_browseRequest; _browseCancellation?.Cancel(); _browseCancellation = null;
        _filePager.Visibility = Visibility.Collapsed;
    }
    private void BeginFolderFiles(int offset)
    {
        CancelFolderBrowse();
        if (_busy || _report is null || !ReferenceEquals(_fileDetailsReport, _report) || _fileDetailsPath is null) return;
        var report = _report; var id = _currentFolderId;
        if (report.Nodes[id].LinkSkipped) return;
        _browseOffset = offset;
        var cancellation = new CancellationTokenSource(); _browseCancellation = cancellation;
        var request = _browseRequest;
        _filePrevious.IsEnabled = _fileNext.IsEnabled = false;
        _filePageLabel.Text = "正在读取文件明细…";
        _filePager.Visibility = Visibility.Visible;
        _ = PrepareFolderFiles(report, id, _fileDetailsPath, offset, _folderPageOffset, request, cancellation);
    }
    private async Task PrepareFolderFiles(ScanReport report, int id, string storedPath, int offset, int directoryOffset, long request, CancellationTokenSource cancellation)
    {
        try
        {
            var page = await Task.Run(() => FileDetailStore.ReadPage(storedPath, id, offset, 1000, cancellation.Token, report.FileDetailsStoreId));
            cancellation.Token.ThrowIfCancellationRequested();
            if (request != _browseRequest || !ReferenceEquals(report, _report) || id != _currentFolderId || _busy) return;
            var directoryRows = (_children.GetValueOrDefault(id) ?? []).Where(_nameFilter)
                .Skip(directoryOffset).Take(FolderPageSize);
            var fileRows = page.Files.Where(_nameFilter);
            _table.ItemsSource = directoryRows.Concat(fileRows)
                .Select(n => new NodeRow(n, PercentBase(report, n, report.Nodes[id].TotalBytes), _directDirectoryCounts.GetValueOrDefault(n.Id), report.MeasuresAllocatedSpace, report.MinimumFileBytes)).ToList();
            _filePrevious.IsEnabled = offset > 0; _fileNext.IsEnabled = page.HasMore;
            _filePageLabel.Text = $"文件第{offset / 1000 + 1}页 · 读取{page.Files.Count:N0}项（排序和文件筛选仅当前页）";
            _filePager.Visibility = page.Files.Count > 0 || offset > 0 || page.HasMore ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            if (request == _browseRequest)
            {
                _table.ItemsSource = null;
                _filePrevious.IsEnabled = false; _fileNext.IsEnabled = false;
                _filePageLabel.Text = "名称筛选超时，文件列表未显示；请清除或简化筛选条件。";
                _filePager.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or System.Security.SecurityException)
        {
            if (request == _browseRequest) { _filePageLabel.Text = "文件明细未读取：" + ex.Message; _filePager.Visibility = Visibility.Visible; }
        }
        finally
        {
            if (ReferenceEquals(_browseCancellation, cancellation)) _browseCancellation = null;
            cancellation.Dispose();
        }
    }
    private async Task PrepareChart(ScanReport report, int id, long request, IEnumerable<ScanNode> nodes, CancellationTokenSource cancellation)
    {
        try
        {
            var summary = await Task.Run(() => ChartSummary.Create(nodes, cancellation.Token));
            if (!ReferenceEquals(_report, report) || request != _chartRequest) return;
            if (_chartSummaries.Count >= 128) _chartSummaries.Clear();
            _chartSummaries[id] = summary;
            _chart.SetSummary(summary);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (OverflowException ex) { if (request == _chartRequest) _status.Text = "图表汇总失败：" + ex.Message; }
        finally { if (ReferenceEquals(_chartCancellation, cancellation)) _chartCancellation = null; cancellation.Dispose(); }
    }
    private Menu BuildMenu()
    {
        var menu = new Menu();
        var file = new MenuItem { Header = "文件" }; menu.Items.Add(file);
        var load = new MenuItem { Header = "打开扫描结果…" }; load.Click += async (_, _) => await LoadReport(); file.Items.Add(load);
        var recent = new MenuItem { Header = "最近三次扫描" };
        recent.SubmenuOpened += (_, _) =>
        {
            recent.Items.Clear();
            try
            {
                foreach (var snapshot in ScanHistory.Recent())
                {
                    var entry = new MenuItem { Header = File.GetLastWriteTime(snapshot).ToString("yyyy-MM-dd HH:mm:ss") };
                    entry.Click += async (_, _) => await LoadReport(snapshot);
                    recent.Items.Add(entry);
                }
                if (recent.Items.Count == 0) recent.Items.Add(new MenuItem { Header = "暂无保存结果", IsEnabled = false });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _status.Text = "历史结果未读取：" + ex.Message; }
        };
        file.Items.Add(recent);
        var save = new MenuItem { Header = "保存扫描结果…" }; save.Click += async (_, _) => await ExportReport(); file.Items.Add(save);
        var export = new MenuItem { Header = "导出报告…" }; export.Click += async (_, _) => await ExportReport(); file.Items.Add(export);
        var exportChart = new MenuItem { Header = "导出图表 PNG…" }; exportChart.Click += (_, _) => ExportChart(); file.Items.Add(exportChart);
        var settings = new MenuItem { Header = "保存当前设置" }; settings.Click += (_, _) => SaveSettings(); file.Items.Add(settings);
        var exit = new MenuItem { Header = "退出" }; exit.Click += (_, _) => Close(); file.Items.Add(exit);
        var scan = new MenuItem { Header = "扫描" }; menu.Items.Add(scan);
        var start = new MenuItem { Header = "扫描目录" }; start.Click += async (_, _) => await StartScan(); scan.Items.Add(start);
        var allocated = new MenuItem { Header = "统计占用空间（下次扫描，硬链接去重）", IsCheckable = true };
        allocated.Click += (_, _) => { if (_busy) { allocated.IsChecked = _measureAllocatedSpace; return; } _measureAllocatedSpace = allocated.IsChecked; };
        scan.Items.Add(allocated);
        scan.Items.Add(_diskMode);
        var memoryBudget = new MenuItem { Header = "内存预算（下次扫描或加载）" };
        foreach (var gib in new[] { 2, 4, 8, 16 })
        {
            var choice = new MenuItem { Header = $"{gib} GiB", IsCheckable = true, Tag = gib };
            choice.Click += (_, _) => { if (!_busy) _memoryBudgetGiB = gib; };
            memoryBudget.Items.Add(choice);
        }
        memoryBudget.SubmenuOpened += (_, _) =>
        {
            foreach (MenuItem choice in memoryBudget.Items) { choice.IsChecked = (int)choice.Tag == _memoryBudgetGiB; choice.IsEnabled = !_busy; }
        };
        scan.Items.Add(memoryBudget);
        var compact = _compactMode;
        compact.Click += (_, _) => { if (_busy) { compact.IsChecked = !_retainFileNodes; return; } _retainFileNodes = !compact.IsChecked; };
        scan.Items.Add(compact);
        var stop = new MenuItem { Header = "停止扫描" }; stop.Click += (_, _) => _cancellation?.Cancel(); scan.Items.Add(stop);
        var filter = new MenuItem { Header = "名称筛选…" }; filter.Click += (_, _) => ConfigureNameFilter(); scan.Items.Add(filter);
        var advanced = new MenuItem { Header = "高级列表筛选…" }; advanced.Click += (_, _) => ConfigureAdvancedFilter(); scan.Items.Add(advanced);
        var clearFilter = new MenuItem { Header = "清除列表筛选" };
        clearFilter.Click += (_, _) =>
        {
            _nameFilter = _ => true; _quickSearchText = ""; _searchInput.Clear(); _nameMasks = _filterDescription = "";
            _filterExclude = false; _advancedFilter = null;
            SelectFolder(_currentFolderId);
        };
        scan.Items.Add(clearFilter);
        var view = new MenuItem { Header = "查看" }; menu.Items.Add(view);
        var allocatedRank = new MenuItem { Header = "全局实际占用排序" };
        allocatedRank.Click += async (_, _) => await ShowReport("全局实际占用排序");
        view.Items.Add(allocatedRank);
        var up = new MenuItem { Header = "上一级" }; up.Click += (_, _) => NavigateUp(); view.Items.Add(up);
        var chart = new MenuItem { Header = "显示图表", IsCheckable = true, IsChecked = true };
        view.SubmenuOpened += (_, _) => chart.IsChecked = _chartRow.Height.Value > 0;
        chart.Click += (_, _) => SetChartVisible(chart.IsChecked); view.Items.Add(chart);
        var issues = new MenuItem { Header = "扫描问题明细…" }; issues.Click += (_, _) => ShowIssues(); view.Items.Add(issues);
        var directFiles = new MenuItem { Header = "读取当前目录文件明细（最多1000项）" }; directFiles.Click += async (_, _) => await LoadDirectFiles(); view.Items.Add(directFiles);
        var detailFirst = new MenuItem { Header = "扫描文件明细：第一页（扫描顺序）" };
        detailFirst.Click += async (_, _) => await LoadDirectFiles(0); view.Items.Add(detailFirst);
        var detailNext = new MenuItem { Header = "扫描文件明细：下一页" };
        detailNext.Click += async (_, _) => { if (_detailPageFolder == _currentFolderId && _detailHasMore) await LoadDirectFiles(_detailPageOffset + 1000); };
        view.Items.Add(detailNext);
        var detailPrevious = new MenuItem { Header = "扫描文件明细：上一页" };
        detailPrevious.Click += async (_, _) => { if (_detailPageFolder == _currentFolderId && _detailPageOffset > 0) await LoadDirectFiles(Math.Max(0, _detailPageOffset - 1000)); };
        view.Items.Add(detailPrevious);
        view.SubmenuOpened += (_, _) =>
        {
            var hasStoredFiles = !_busy && _report is not null && ReferenceEquals(_report, _fileDetailsReport) && _fileDetailsPath is not null;
            detailFirst.IsEnabled = hasStoredFiles;
            detailNext.IsEnabled = hasStoredFiles && _detailPageFolder == _currentFolderId && _detailHasMore;
            detailPrevious.IsEnabled = hasStoredFiles && _detailPageFolder == _currentFolderId && _detailPageOffset > 0;
        };
#if DEBUG
        var diagnostics = new MenuItem { Header = "保护验证" }; menu.Items.Add(diagnostics);
        var hangProbe = new MenuItem { Header = "验证持续无响应退出（本测试实例）" };
        hangProbe.Click += (_, _) =>
        {
            if (_busy) return;
            Title = "Flash-X · 180秒无响应保护测试";
            Dispatcher.BeginInvoke(new Action(() => System.Threading.Thread.Sleep(TimeSpan.FromMinutes(4))), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        diagnostics.Items.Add(hangProbe);
#endif
        var reports = new MenuItem { Header = "报表" }; menu.Items.Add(reports);
        foreach (var kind in new[] { "文件和目录", "最大文件", "最大目录", "最大文件和目录", "最旧文件", "最旧目录", "最旧文件和目录", "最新文件", "最新目录", "最新文件和目录", "最长路径" })
        { var item = new MenuItem { Header = kind }; item.Click += async (_, _) => await ShowReport(kind); reports.Items.Add(item); }
        return menu;
    }
    private async Task LoadDirectFiles(int? pageOffset = null)
    {
        if (_report is null || _busy) return;
        CancelFolderBrowse();
        var report = _report; var id = _currentFolderId; var path = report.Nodes[id].Path;
        _busy = true; _scan.IsEnabled = _export.IsEnabled = false;
        _cancellation = new CancellationTokenSource(); _cancel.IsEnabled = true;
        var token = _cancellation.Token;
        try
        {
            var storedPath = ReferenceEquals(report, _fileDetailsReport) ? _fileDetailsPath : null;
            if (pageOffset.HasValue && storedPath is null) { _status.Text = "当前结果没有磁盘明细，无法翻页。"; return; }
            var page = await Task.Run(() => storedPath is null ? DirectoryFileReader.Read(path, 1000, token, report.MinimumFileBytes)
                : StoredPage(pageOffset.HasValue ? FileDetailStore.ReadPage(storedPath, id, pageOffset.Value, 1000, token, report.FileDetailsStoreId) : FileDetailStore.ReadLargest(storedPath, id, 1000, token, report.FileDetailsStoreId)));
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(report, _report) || id != _currentFolderId) return;
            var rows = page.Files.Where(_nameFilter).Select(n => new NodeRow(n, PercentBase(report, n, 0), 0, storedPath is not null && report.MeasuresAllocatedSpace, report.MinimumFileBytes)).ToList();
            _table.ItemsSource = rows; _folderPager.Visibility = Visibility.Collapsed;
            _detailPageFolder = pageOffset.HasValue ? id : -1; _detailPageOffset = pageOffset ?? 0; _detailHasMore = page.Truncated;
            _selectedPath.Text = path + (storedPath is null ? " · 实时文件明细（不属于扫描快照）" : pageOffset.HasValue ? $" · 扫描明细第{pageOffset.Value / 1000 + 1}页（扫描顺序，排序仅当前页）" : " · 扫描时保存的文件明细（大小前1000项）");
            if (_filterDescription.Length > 0) _selectedPath.Text += " · 仅筛选本次读取的至多1000项：" + _filterDescription + "（不代表全部文件的匹配结果）";
            _status.Text = $"已读取 {page.Files.Count} 项，筛选后显示 {rows.Count} 项；跳过文件链接 {page.SkippedLinks} 项" + (page.Truncated ? (pageOffset.HasValue ? "，可使用下一页继续查看。" : "，当前仅显示1000项，扫描明细可使用分页查看。") : "。");
            _chartCancellation?.Cancel(); ++_chartRequest; _chart.SetSummary([]);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            _table.ItemsSource = null;
            _detailPageFolder = -1; _detailHasMore = false;
            _chartCancellation?.Cancel(); ++_chartRequest; _chart.SetSummary([]);
            _status.Text = "名称筛选超时，文件明细未显示；请清除或简化筛选条件。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { _status.Text = "文件明细读取已取消。"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or System.Security.SecurityException)
        { _status.Text = "文件明细未读取：" + ex.Message; }
        finally { _busy = false; _scan.IsEnabled = true; _export.IsEnabled = _report != null; _cancel.IsEnabled = false; _cancellation.Dispose(); _cancellation = null; }
    }
    private static DirectoryFilePage StoredPage(StoredFilePage page) => new(page.Files, page.HasMore);
    private void ShowIssues()
    {
        if (_report is null) { _status.Text = "尚无扫描结果。"; return; }
        var dialog = new Window { Owner = this, Title = "扫描问题明细", Width = 950, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(10) };
        var summary = new TextBlock { Text = $"扫描问题 {_report.Issues.Count + _report.SuppressedIssueCount:N0} 条（含读取失败及安全限制）；保留 {_report.Issues.Count:N0} 条明细，省略 {_report.SuppressedIssueCount:N0} 条。跳过链接单独统计。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(summary, Dock.Top); panel.Children.Add(summary);
        var table = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, ItemsSource = _report.Issues.Select(issue => new { issue.Path, issue.Operation, issue.Message, Category = ScanDiagnostics.DescribeIssue(issue), issue.ErrorCode }).ToList() };
        table.Columns.Add(new DataGridTextColumn { Header = "路径", Binding = new Binding("Path"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        table.Columns.Add(new DataGridTextColumn { Header = "类别", Binding = new Binding("Category"), Width = 155 });
        table.Columns.Add(new DataGridTextColumn { Header = "阶段", Binding = new Binding("Operation"), Width = 110 });
        table.Columns.Add(new DataGridTextColumn { Header = "原因", Binding = new Binding("Message"), Width = new DataGridLength(3, DataGridLengthUnitType.Star) });
        var copy = MakeButton("复制问题明细");
        DockPanel.SetDock(copy, Dock.Bottom); panel.Children.Add(copy);
        var report = _report;
        copy.Click += (_, _) =>
        {
            try
            {
                var text = new System.Text.StringBuilder();
                text.AppendLine($"扫描问题 {report.Issues.Count + report.SuppressedIssueCount} 条，省略 {report.SuppressedIssueCount} 条明细");
                foreach (var issue in report.Issues) text.AppendLine($"{issue.Operation}\t{issue.Path}\t{issue.Message}");
                Clipboard.SetText(text.ToString());
                summary.Text = "已复制保留的问题明细；未读取的内容仍不计入扫描大小。";
            }
            catch (System.Runtime.InteropServices.ExternalException ex) { summary.Text = "剪贴板暂不可用：" + ex.Message; }
        };
        panel.Children.Add(table); dialog.Content = panel; dialog.ShowDialog();
    }
    private void NavigateUp()
    {
        if (_report is null) return;
        var parent = _report.Nodes[_currentFolderId].ParentId;
        if (parent.HasValue) SelectFolder(parent.Value);
    }
    private void OpenInExplorer(string directory)
    {
        try
        {
            if (string.Equals(directory, "shell:MyComputerFolder", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:MyComputerFolder") { UseShellExecute = true });
                return;
            }
            if (!Directory.Exists(directory) && !File.Exists(directory)) throw new FileNotFoundException("路径已不存在或无法访问：" + directory);
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            if (Directory.Exists(directory)) start.ArgumentList.Add(System.IO.Path.GetFullPath(directory));
            else start.Arguments = "/select,\"" + System.IO.Path.GetFullPath(directory) + "\"";
            Process.Start(start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { _status.Text = "无法打开资源管理器：" + ex.Message; }
    }
    private void AddAnalysisActions(ContextMenu menu, Func<ScanNode?> selected)
    {
        var delete = new MenuItem { Header = "删除（移到回收站）…" };
        delete.Click += (_, _) => { if (selected() is ScanNode node) DeleteSelected(node); };
        var analyze = new MenuItem { Header = "AI 分析…" };
        analyze.Click += (_, _) =>
        {
            if (selected() is not ScanNode node || _report is not { } report) return;
            _analysisChat.SetTarget(report, node.Id, node.Path, () => BuildAnalysisMaterial(report, node));
            _analysisChat.FocusComposer();
        };
        menu.Items.Add(delete);
        menu.Items.Add(analyze);
    }

    private string BuildAnalysisMaterial(ScanReport report, ScanNode node)
    {
        var state = NodeSizeStatus.Get(node) switch
        {
            NodeSizeState.Known => "已读取",
            NodeSizeState.Partial => "部分读取",
            NodeSizeState.SkippedLink => "链接已跳过",
            _ => "大小未知"
        };
        var text = new System.Text.StringBuilder();
        text.AppendLine($"路径：{node.Path}");
        text.AppendLine($"类型：{(node.IsDirectory ? "目录" : "文件")}");
        text.AppendLine($"逻辑大小：{DisplayNodeSize(node, report.MinimumFileBytes)}");
        text.AppendLine($"去重累计占用字节：{(report.MeasuresAllocatedSpace ? node.TotalAllocatedBytes?.ToString() ?? "未知" : "未统计")}");
        text.AppendLine($"文件数：{node.TotalFiles}；子目录数：{node.TotalDirectories}");
        text.AppendLine($"读取状态：{state}。{ScanDiagnostics.DescribeNode(node)}");
        text.AppendLine($"扫描时间：{report.FinishedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}。整份扫描{(report.Complete ? "完整" : "有未读项目")}。材料来自快照，未读取文件内容。");
        if (report.MinimumFileBytes > 0)
            text.AppendLine($"快速估算仅计入逻辑长度严格大于 {report.MinimumFileBytes} 字节的文件。目录统计仅覆盖计入项，零值无法证明目录为空。");
        if (node.HardLinkDuplicate)
            text.AppendLine("此路径为重复硬链接，去重累计占用已计入其他路径。");
        if (node.IsDirectory && _children.TryGetValue(node.Id, out var children) && children.Count > 0)
        {
            text.AppendLine("扫描快照中的直接子项（优先列出大项，最多 12 项）：");
            foreach (var child in children.Take(12))
                text.AppendLine($"- {child.Path}｜{(child.IsDirectory ? "目录" : "文件")}｜逻辑大小 {DisplayNodeSize(child, report.MinimumFileBytes)}");
        }
        return text.ToString();
    }

    private void DeleteSelected(ScanNode node)
    {
        try
        {
            if (_busy) { _status.Text = "请先结束扫描，再删除。"; return; }
            var path = System.IO.Path.GetFullPath(node.Path);
            if (System.IO.Path.TrimEndingDirectorySeparator(path).Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetPathRoot(path)!), StringComparison.OrdinalIgnoreCase))
                throw new IOException("不能删除磁盘根目录。");
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("链接或重解析项请在资源管理器中处理。");
            if (MessageBox.Show(this, "将以下项目移到回收站？目录包含其全部内容。\n\n" + path, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if ((attributes & FileAttributes.Directory) != 0)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.AllDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.AllDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            _status.Text = "删除操作已完成；当前结果是旧快照，请手动重新扫描核对。";
        }
        catch (OperationCanceledException) { _status.Text = "删除已取消；如有部分操作，请在资源管理器核对。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { _status.Text = "删除未完成：" + ex.Message + " · 目录可能已有部分项目被处理，请在资源管理器核对并重新扫描。"; }
    }
    private void SaveSettings()
    {
        try
        {
            new DesktopSettings { Width = RestoreBounds.Width, Height = RestoreBounds.Height,
                LastPath = _path.Text, NameMasks = _nameMasks, Exclude = _filterExclude, Bars = _chart.Bars, ShowChart = _chartRow.Height.Value > 0, AdvancedFilter = _advancedFilter, RetainFileNodes = _retainFileNodes, MemoryBudgetGiB = _memoryBudgetGiB }.Save();
            _status.Text = "设置已保存：" + DesktopSettings.FilePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { _status.Text = "设置未保存：" + ex.Message; }
    }
    private void SetChartVisible(bool visible)
    {
        _chartRow.MinHeight = visible ? 140 : 0;
        _chartRow.Height = new GridLength(visible ? 220 : 0);
        _chartSplitterRow.Height = new GridLength(visible ? 6 : 0);
        foreach (UIElement child in _details.Children)
            if (Grid.GetRow(child) >= 2) child.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SetProgress(bool running)
    {
        _progress.IsIndeterminate = false;
        _progress.Value = 0;
        _progress.Visibility = Visibility.Collapsed;
    }
    private void ConfigureNameFilter()
    {
        var dialog = new Window { Owner = this, Title = "名称筛选", Width = 460, Height = 230, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(15) };
        panel.Children.Add(new TextBlock { Text = "通配符：* 任意字符，? 单个字符，多个规则用 ; 分隔", TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = _nameMasks, Margin = new Thickness(0, 12, 0, 8) }; panel.Children.Add(input);
        var exclude = new CheckBox { Content = "排除匹配项", IsChecked = _filterExclude }; panel.Children.Add(exclude);
        panel.Children.Add(new TextBlock { Text = "仅筛选列表；扫描累计、目录树、图表和导出不受影响。", Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap });
        var apply = MakeButton("应用"); panel.Children.Add(apply); dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        apply.Click += (_, _) =>
        {
            try { _nameFilter = NameFilter.Create(input.Text, exclude.IsChecked == true); _nameMasks = input.Text; _advancedFilter = null; _filterExclude = exclude.IsChecked == true; _filterDescription = input.Text; SelectFolder(_currentFolderId); dialog.Close(); }
            catch (Exception ex) when (ex is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException) { MessageBox.Show(dialog, ex is System.Text.RegularExpressions.RegexMatchTimeoutException ? "名称筛选耗时过长，请缩短或简化筛选条件后重试。" : ex.Message); }
        };
        dialog.ShowDialog();
    }
    private void ConfigureAdvancedFilter()
    {
        var dialog = new Window { Owner = this, Title = "高级列表筛选", Width = 490, Height = 530, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(15) };
        TextBox Input(string caption)
        {
            panel.Children.Add(new TextBlock { Text = caption });
            var field = new TextBox { Margin = new Thickness(0, 4, 0, 10) }; panel.Children.Add(field); return field;
        }
        var masks = Input("名称规则（*、?，多个规则用分号分隔；留空为全部）"); masks.Text = _advancedFilter?.NameMasks ?? _nameMasks;
        var minimum = Input("最小大小（字节，包含边界；留空不限制）");
        var maximum = Input("最大大小（字节，包含边界；留空不限制）");
        minimum.Text = _advancedFilter?.MinimumBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        maximum.Text = _advancedFilter?.MaximumBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        var from = Input("修改时间起点（本地时间 yyyy-MM-dd HH:mm:ss，留空不限制）");
        var to = Input("修改时间终点（包含该秒，留空不限制）");
        from.Text = _advancedFilter?.ModifiedFromUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";
        to.Text = _advancedFilter?.ModifiedToUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";
        var scope = new ComboBox { ItemsSource = new[] { "文件和目录", "仅文件", "仅目录" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(scope);
        scope.SelectedIndex = _advancedFilter?.Scope == ReportScope.Files ? 1 : _advancedFilter?.Scope == ReportScope.Directories ? 2 : 0;
        panel.Children.Add(new TextBlock { Text = "仅筛选列表；目录大小为累计值，图表、扫描总量和导出保持完整结果。", TextWrapping = TextWrapping.Wrap });
        var apply = MakeButton("应用"); panel.Children.Add(apply); dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        apply.Click += (_, _) =>
        {
            try
            {
                long? ParseBound(string text) => string.IsNullOrWhiteSpace(text) ? null : long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                DateTime? ParseTime(string text, bool end) => string.IsNullOrWhiteSpace(text) ? null : DateTime.ParseExact(text.Trim(), "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal).ToUniversalTime().AddTicks(end ? TimeSpan.TicksPerSecond - 1 : 0);
                var options = new NodeFilterOptions { NameMasks = masks.Text, MinimumBytes = ParseBound(minimum.Text), MaximumBytes = ParseBound(maximum.Text), ModifiedFromUtc = ParseTime(from.Text, false), ModifiedToUtc = ParseTime(to.Text, true), Scope = scope.SelectedIndex == 1 ? ReportScope.Files : scope.SelectedIndex == 2 ? ReportScope.Directories : ReportScope.All };
                _nameFilter = NodeFilter.Create(options);
                _advancedFilter = options;
                _nameMasks = masks.Text; _filterExclude = false;
                _filterDescription = DescribeFilter(options);
                SelectFolder(_currentFolderId); dialog.Close();
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            { MessageBox.Show(dialog, ex.Message, "筛选条件无效"); }
        };
        dialog.ShowDialog();
    }
    private static string DescribeFilter(NodeFilterOptions options)
    {
        var scope = options.Scope switch { ReportScope.Files => "仅文件", ReportScope.Directories => "仅目录", _ => "文件和目录" };
        var text = $"高级：{scope}；名称={(options.NameMasks.Length == 0 ? "全部" : options.NameMasks)}；大小={options.MinimumBytes?.ToString() ?? "不限"}至{options.MaximumBytes?.ToString() ?? "不限"}字节";
        if (options.ModifiedFromUtc.HasValue || options.ModifiedToUtc.HasValue)
            text += $"；修改时间={options.ModifiedFromUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "不限"}至{options.ModifiedToUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "不限"}";
        return text;
    }
    private static IEnumerable<ScanHistoryOverview.OverviewEntry> OverviewEntries(ScanHistoryOverview overview) =>
        overview.RootPath == ComputerScanner.SelectionName
            ? overview.Entries.OrderBy(entry => entry.Node.Path, StringComparer.OrdinalIgnoreCase)
            : overview.Entries;

    private void SetHistoryRecord(string target, DateTime finishedUtc)
    {
        _historyFinishedUtc = finishedUtc;
        var name = target == ComputerScanner.SelectionName ? "整个电脑" : target;
        _historyLabel.Text = $"{finishedUtc.ToLocalTime():MM-dd HH:mm} · {name}";
        _historySelector.ToolTip = $"{finishedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {name}\n点击切换最近三次扫描记录";
    }

    private void ShowHistoryOverview(ScanHistoryOverview overview)
    {
        SetHistoryRecord(overview.RootPath, overview.FinishedUtc);
        _summary.Text = (overview.Complete ? "" : "部分结果 · ")
            + (overview.MinimumFileBytes > 0 ? $"快速估算（> {FormatBytes(overview.MinimumFileBytes)}） · " : "")
            + $"{DisplayNodeSize(overview.Root, overview.MinimumFileBytes)} · {overview.Root.TotalFiles:N0} 个文件 · {overview.Root.TotalDirectories:N0} 个子目录";
        if (overview.MeasuresAllocatedSpace)
            _summary.Text += " · 占用 " + (overview.Root.TotalAllocatedBytes is long bytes ? FormatBytes(bytes) : "未知（部分未读取）");

        _historyPreview = overview;
        _previewNodeId = 0;
        var root = PreviewTreeNode(overview, overview.Root, overview.Root.TotalBytes);
        foreach (var entry in OverviewEntries(overview).Where(entry => entry.Node.IsDirectory))
            root.Items.Add(PreviewTreeNode(overview, entry.Node, entry.Node.TotalBytes));
        _tree.Items.Clear();
        _tree.Items.Add(root);
        root.IsExpanded = true;
        _tree.IsEnabled = overview.DrivePages is not null;
        _allocatedColumn.Visibility = overview.MeasuresAllocatedSpace ? Visibility.Visible : Visibility.Collapsed;
        _table.IsEnabled = false;
        root.IsSelected = true;
        _status.Text = overview.DrivePages is null
            ? "已显示扫描概览 · 正在恢复完整目录…"
            : "已显示扫描概览 · 可先展开盘符浏览，完整目录正在后台载入…";
    }

    private static ScanHistoryOverview.DrivePage? PreviewPage(ScanHistoryOverview overview, int id) =>
        overview.DrivePages?.GetValueOrDefault(id) ?? overview.FolderPages?.GetValueOrDefault(id);

    private TreeViewItem PreviewTreeNode(ScanHistoryOverview overview, ScanNode node, long driveBytes)
    {
        var item = new TreeViewItem
        {
            Tag = node.Id,
            DataContext = (node, driveBytes)
        };
        item.Header = FolderHeader(item, $"{node.Name}   {DisplayNodeSize(node, overview.MinimumFileBytes)}");
        if (PreviewPage(overview, node.Id) is { } page && page.Entries.Any(entry => entry.Node.IsDirectory))
        {
            item.Items.Add("…");
            item.Expanded += (_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, item) || item.Items.Count != 1 || item.Items[0] is not string) return;
                item.Items.Clear();
                foreach (var entry in page.Entries.Where(entry => entry.Node.IsDirectory))
                    item.Items.Add(PreviewTreeNode(overview, entry.Node, driveBytes));
            };
        }
        return item;
    }

    private void ShowHistoryPreviewRows(ScanHistoryOverview overview, ScanNode node, long driveBytes)
    {
        var id = node.Id;
        if (id == 0)
        {
            _previewNodeId = 0;
            _table.ItemsSource = OverviewEntries(overview).Select(entry => new NodeRow(entry.Node,
                overview.RootPath == ComputerScanner.SelectionName ? entry.Node.TotalBytes : overview.Root.TotalBytes,
                entry.DirectDirectories, overview.MeasuresAllocatedSpace, overview.MinimumFileBytes)).ToList();
            _selectedPath.Text = overview.RootPath + (overview.HasMore ? " · 概览最多显示 100 项" : " · 扫描概览");
        }
        else
        {
            _previewNodeId = id;
            var page = PreviewPage(overview, id);
            _table.ItemsSource = page?.Entries.Select(entry => new NodeRow(entry.Node, driveBytes,
                entry.DirectDirectories, overview.MeasuresAllocatedSpace, overview.MinimumFileBytes)).ToList();
            _selectedPath.Text = node.Path + (page is null ? " · 正在载入所选目录…" : page.HasMore
                ? $" · 扫描概览前 {page.Entries.Length} 项" : " · 扫描概览");
        }
    }

    private static void CollectExpandedPreviewNodes(ItemsControl parent, HashSet<int> expanded)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>())
        {
            if (!item.IsExpanded || item.Tag is not int id) continue;
            expanded.Add(id);
            CollectExpandedPreviewNodes(item, expanded);
        }
    }

    private static void RestoreExpandedPreviewNodes(ItemsControl parent, HashSet<int> expanded)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>())
        {
            if (item.Tag is not int id || !expanded.Contains(id)) continue;
            item.IsExpanded = true;
            RestoreExpandedPreviewNodes(item, expanded);
        }
    }

    private async Task LoadReport(string? source = null, bool automatic = false)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = "打开 Flash-X 扫描结果", Filter = "Flash-X JSON|*.json" };
        if (source is null && dialog.ShowDialog(this) != true) return;
        var snapshotFile = Path.GetFullPath(source ?? dialog.FileName);
        Action? restorePreview = null;
        int? resumeBrowseOffset = null;
        var reportChanged = false;
        _busy = true; _scan.IsEnabled = _export.IsEnabled = false;
        _cancellation = new CancellationTokenSource(); _cancel.IsEnabled = true;
        var token = _cancellation.Token;
        var memoryBudgetBytes = (long)_memoryBudgetGiB * 1024 * 1024 * 1024;
        SetProgress(true);
        try
        {
            var stamp = ScanHistoryOverview.SnapshotStamp(snapshotFile);
            if (_loadedSnapshot is { } loaded
                && string.Equals(loaded.Path, snapshotFile, StringComparison.OrdinalIgnoreCase)
                && (loaded.Length, loaded.WriteUtc) == stamp)
            {
                _status.Text = "已显示该扫描记录";
                return;
            }

            resumeBrowseOffset = _browseCancellation is null ? null : _browseOffset;
            _status.Text = automatic ? "正在恢复上次扫描结果…" : "正在载入扫描记录…";
            var overview = await Task.Run(() => ScanHistoryOverview.TryRead(snapshotFile), token);
            token.ThrowIfCancellationRequested();
            if (overview is not null)
            {
                var previousTree = _tree.Items.Cast<object>().ToArray();
                var previousRows = _table.ItemsSource;
                var previousSelection = _tree.SelectedItem as TreeViewItem;
                var previousFilePager = _filePager.Visibility;
                var previousPath = _selectedPath.Text;
                var previousSummary = _summary.Text;
                var previousAllocation = _allocatedColumn.Visibility;
                var previousTime = _historyFinishedUtc;
                var previousLabel = _historyLabel.Text;
                var previousHint = _historySelector.ToolTip;
                var previousFolderPager = _folderPager.Visibility;
                restorePreview = () =>
                {
                    _restoringHistoryView = true;
                    try
                    {
                        _tree.Items.Clear();
                        foreach (var item in previousTree) _tree.Items.Add(item);
                        if (previousSelection is not null && !ReferenceEquals(_tree.SelectedItem, previousSelection))
                        {
                            previousSelection.IsSelected = false;
                            previousSelection.IsSelected = true;
                        }
                    }
                    finally { _restoringHistoryView = false; }
                    _table.ItemsSource = previousRows;
                    _filePager.Visibility = previousFilePager;
                    _selectedPath.Text = previousPath;
                    _summary.Text = previousSummary;
                    _allocatedColumn.Visibility = previousAllocation;
                    _historyFinishedUtc = previousTime;
                    _historyLabel.Text = previousLabel;
                    _historySelector.ToolTip = previousHint;
                    _folderPager.Visibility = previousFolderPager;
                };
                CancelFolderBrowse();
                _folderPager.Visibility = Visibility.Collapsed;
                _analysisChat.IsEnabled = false;
                ShowHistoryOverview(overview);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
            var (report, index, detailStatus) = await Task.Run(() =>
            {
                var writeOverview = (overview is null || overview.RootPath == ComputerScanner.SelectionName && overview.FolderPages is null)
                    && (automatic || ScanHistory.IsStoredSnapshot(snapshotFile));

                var report = ReportReader.Read(snapshotFile, maximumNodes: (int)Math.Min(10000000L, memoryBudgetBytes / 1024), cancellationToken: token, maximumManagedBytes: memoryBudgetBytes, maximumSnapshotBytes: memoryBudgetBytes / 2);
                var index = DirectoryIndex.Create(report, cancellationToken: token, sortChildren: !report.Cancelled, maximumManagedBytes: memoryBudgetBytes);
                token.ThrowIfCancellationRequested();
                var detailStatus = "";
                if (report.FileDetailsPath is string detailPath)
                {
                    try
                    {
                        FileDetailStore.ValidateAssociation(detailPath, report.FileDetailsStoreId);
                        detailStatus = report.FileDetailsStoreId is null ? " · 旧版明细：关联未经验证" : " · 文件明细关联已核对";
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                    { detailStatus = " · 文件明细不可用：" + ex.Message; }
                }
                if (writeOverview) ScanHistoryOverview.TryWrite(snapshotFile, stamp, report, index);
                return (report, index, detailStatus);
            });
            token.ThrowIfCancellationRequested();
            restorePreview = null;
            _loadedSnapshot = (snapshotFile, stamp.Length, stamp.WriteUtc);
            reportChanged = true;
            var selectedNode = _historyPreview is null ? 0 : _previewNodeId;
            var expandedNodes = new HashSet<int>();
            if (_historyPreview is not null) CollectExpandedPreviewNodes(_tree, expandedNodes);
            _historyPreview = null;
            _chartCancellation?.Cancel(); ++_chartRequest; _chartSummaries.Clear(); CancelFolderBrowse(); _detailPageFolder = -1; _detailPageOffset = 0; _detailHasMore = false; _report = report; SetHistoryRecord(report.RootPath, report.FinishedUtc); if (_resultFilterPanel is not null) _resultFilterPanel.IsEnabled = true; _allocatedColumn.Visibility = report.MeasuresAllocatedSpace ? Visibility.Visible : Visibility.Collapsed; _children = index.Children;
            if (report.RootPath == ComputerScanner.SelectionName && _children.TryGetValue(0, out var drivesInOrder))
                drivesInOrder.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path)); _directDirectoryCounts = index.DirectDirectories; _path.Text = _scanTarget;
            _fileDetailsPath = report.FileDetailsPath; _fileDetailsReport = report.FileDetailsPath is null ? null : report;
            _tree.Items.Clear();
            var root = TreeNode(report.Root);
            _tree.Items.Add(root);
            root.IsExpanded = true;
            RestoreExpandedPreviewNodes(root, expandedNodes);
            var ancestors = new Stack<int>();
            for (var id = selectedNode; id > 0 && id < report.Nodes.Count; id = report.Nodes[id].ParentId ?? 0)
                ancestors.Push(id);
            var selectedItem = root;
            while (ancestors.TryPop(out var ancestor))
            {
                selectedItem.IsExpanded = true;
                var child = selectedItem.Items.OfType<TreeViewItem>().FirstOrDefault(item => item.Tag is int id && id == ancestor);
                if (child is null) break;
                selectedItem = child;
            }
            selectedItem.IsSelected = true;
            if (_currentFolderId != (int)selectedItem.Tag) SelectFolder((int)selectedItem.Tag);
            _summary.Text = (report.Complete ? "" : "部分结果 · ") + (report.MinimumFileBytes > 0 ? $"快速估算（仅累计 > {FormatBytes(report.MinimumFileBytes)} 的文件） · " : "") + $"{DisplayNodeSize(report.Root, report.MinimumFileBytes)} · {report.Root.TotalFiles:N0} 个文件 · {report.Root.TotalDirectories:N0} 个子目录";
            if (report.MeasuresAllocatedSpace) _summary.Text += " · 占用 " + (report.Root.TotalAllocatedBytes is long allocatedBytes ? FormatBytes(allocatedBytes) : "未知（部分未读取）");
            _status.Text = $"已加载快照（不代表当前磁盘状态）· 完整：{(report.Complete ? "是" : "否")} · {report.FinishedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}" + detailStatus;
        }
        catch (OperationCanceledException) { _status.Text = "已取消加载，保留原扫描结果。"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { _status.Text = "结果未恢复，原始文件仍保留：" + ex.Message; if (!automatic) MessageBox.Show(this, ex.Message, "未加载", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally
        {
            _historyPreview = null;
            restorePreview?.Invoke();
            _tree.IsEnabled = _table.IsEnabled = _analysisChat.IsEnabled = true;
            _cancel.IsEnabled = false;
            _cancellation.Dispose();
            _cancellation = null;
            _busy = false;
            _scan.IsEnabled = true;
            _export.IsEnabled = _report != null;
            SetProgress(false);
            if (reportChanged) BeginFolderFiles(0);
            else if (resumeBrowseOffset is int offset) BeginFolderFiles(offset);
        }
    }
    private async Task ShowReport(string kind)
    {
        if (_report is null || _busy) return;
        CancelFolderBrowse();
        if (!_report.RetainsFileNodes && kind != "文件和目录" && (kind.EndsWith("文件") || kind.EndsWith("文件和目录") || kind == "最长路径"))
        { _status.Text = "目录汇总模式未保留文件明细，不能生成该完整报表。"; return; }
        if (kind == "文件和目录") { SelectFolder(_currentFolderId); return; }
        var scope = kind.EndsWith("文件和目录") || kind == "最长路径" ? ReportScope.All : kind.EndsWith("目录") ? ReportScope.Directories : ReportScope.Files;
        var byAllocation = kind == "全局实际占用排序";
        if (byAllocation && !_report.MeasuresAllocatedSpace)
        {
            _status.Text = "此记录未统计实际占用。请在“扫描”菜单开启统计后重新扫描。";
            return;
        }
        var globalSize = kind == "全局大小筛选" || byAllocation;
        if (globalSize) scope = ReportScope.All;
        var order = byAllocation ? ReportOrder.LargestAllocated : globalSize || kind.StartsWith("最大") ? ReportOrder.Largest : kind.StartsWith("最旧") ? ReportOrder.Oldest : kind.StartsWith("最新") ? ReportOrder.Newest : ReportOrder.LongestPath;
        var report = _report;
        var filter = _nameFilter;
        var directoryCounts = _directDirectoryCounts;
        _busy = true; _scan.IsEnabled = _export.IsEnabled = false;
        _status.Text = globalSize ? "正在筛选整份扫描结果…" : "正在后台生成报表…";
        _cancellation = new CancellationTokenSource(); _cancel.IsEnabled = true;
        var token = _cancellation.Token;
        List<NodeRow> rows;
        bool partialGlobalResult;
        string? missingDetails;
        try
        {
            (rows, partialGlobalResult, missingDetails) = await Task.Run(() =>
            {
                IEnumerable<ScanNode> directories = report.Nodes.Skip(1);
                List<NodeRow> SelectRows(IEnumerable<ScanNode> candidates) =>
                    ReportQuery.SelectTop(candidates, scope, order, 10000, filter, token)
                        .Select(n => new NodeRow(n, PercentBase(report, n, report.Root.TotalBytes),
                            directoryCounts.GetValueOrDefault(n.Id), report.MeasuresAllocatedSpace, report.MinimumFileBytes)).ToList();

                var partial = globalSize && !report.Complete;
                if (!globalSize || report.RetainsFileNodes) return (SelectRows(directories), partial, (string?)null);
                if (report.FileDetailsPath is not string details)
                    return (SelectRows(directories), partial, "未保存文件明细");
                try
                {
                    return (SelectRows(directories.Concat(FileDetailStore.ReadAllFiles(details, report.FileDetailsStoreId, token))),
                        partial, (string?)null);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                {
                    return (SelectRows(directories), partial, "文件明细不可读");
                }
            });
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { _status.Text = "报表已取消，保留之前视图。"; return; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        { _status.Text = "报表未生成，保留之前视图：" + ex.Message; return; }
        finally { _busy = false; _scan.IsEnabled = true; _export.IsEnabled = _report != null; _cancel.IsEnabled = false; _cancellation.Dispose(); _cancellation = null; }
        if (!ReferenceEquals(_report, report)) return;
        _selectedPath.Text = kind + "（最多显示10000项，临时限制） · " + _report.RootPath + (order is ReportOrder.Oldest or ReportOrder.Newest ? " · 当前按修改时间" : "");
        if (byAllocation) _selectedPath.Text += " · 占用未知项不参与排序，硬链接按去重占用";
        if (missingDetails is not null) _selectedPath.Text += " · 仅显示目录：" + missingDetails;
        if (report.MinimumFileBytes > 0) _selectedPath.Text += $" · 快速估算门槛 > {report.MinimumFileBytes / 1048576} MiB，估算仅累计符合门槛的文件";
        if (_filterDescription.Length > 0)
            _selectedPath.Text += " · 列表筛选：" + (_filterExclude ? "排除 " : "") + _filterDescription + "（扫描总量和导出为全部项）";
        _table.ItemsSource = rows; _folderPager.Visibility = Visibility.Collapsed;
        _filePager.Visibility = Visibility.Collapsed;
        _status.Text = (globalSize ? "筛选完成 · 显示 " : "报表已生成 · 显示 ") + rows.Count.ToString("N0") + " 项" + (rows.Count == 10000 ? "（显示上限 10,000 项）" : "");
        if (missingDetails is not null) _status.Text += " · 仅筛选目录，" + missingDetails;
        if (partialGlobalResult)
            _status.Text += " · 原扫描不完整，仍可能有其他项目达到门槛";
        _chartCancellation?.Cancel(); ++_chartRequest; _chart.SetNodes([]); // A global report is not a disjoint directory breakdown.
    }
    private async Task ExportReport()
    {
        if (_report is null || _busy) return;
        CancelFolderBrowse();
        var dialog = new SaveFileDialog { Title = "导出扫描结果或 AI 分析摘要", FileName = "ff-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), DefaultExt = ".md", Filter = "AI 分析摘要（Markdown）|*.md|JSON 扫描快照|*.json|CSV 统计表|*.csv|XML 统计表|*.xml", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        var report = _report;
        _export.IsEnabled = _scan.IsEnabled = false;
        _busy = true; _status.Text = "正在导出报告…";
        SetProgress(true);
        try
        {
            await Task.Run(() =>
            {
                if (string.Equals(Path.GetExtension(dialog.FileName), ".md", StringComparison.OrdinalIgnoreCase)) AnalysisReportWriter.Write(report, dialog.FileName);
                else ReportWriter.Write(report, dialog.FileName);
            });
            _status.Text = "已导出：" + dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, ex.Message, "未导出", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { _busy = false; _export.IsEnabled = _scan.IsEnabled = true; SetProgress(false); }
    }
    private void ExportChart()
    {
        if (_busy || _report is null) return;
        if (!_chart.HasData) { _status.Text = "当前视图没有可导出的占比图表。"; return; }
        if (_chart.ActualWidth <= 0 || _chart.ActualHeight <= 0 || _chart.Visibility != Visibility.Visible || _chartRow.Height.Value == 0)
        { _status.Text = "请先显示图表再导出。"; return; }
        var dialog = new SaveFileDialog { Title = "导出当前图表为新 PNG", Filter = "PNG 图片|*.png", DefaultExt = ".png", FileName = "folderscope-chart-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dialog.FileName)!, ".folderscope-chart-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var dpi = VisualTreeHelper.GetDpi(_chart);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(_chart.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(_chart.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(_chart);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { encoder.Save(stream); stream.Flush(true); }
            File.Move(temporary, dialog.FileName, false);
            _status.Text = "图表已导出：" + dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { _status.Text = "图表未导出：" + ex.Message; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static Button MakeButton(string name) => new() { Content = name, Padding = new Thickness(13, 4, 13, 4), Margin = new Thickness(6, 0, 0, 0) };
    private static long PercentBase(ScanReport report, ScanNode node, long fallback)
    {
        if (report.RootPath != ComputerScanner.SelectionName) return fallback;
        var current = node;
        while (current.ParentId is int parent && parent > 0) current = report.Nodes[parent];
        return current.ParentId == 0 ? current.TotalBytes : 0;
    }
    private sealed class LiveScanPreview : IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<ScanPreview> _pending = new();
        private readonly Dictionary<string, TreeViewItem> _items = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Windows.Threading.DispatcherTimer _timer;
        private readonly TreeViewItem _root = new() { Header = "扫描中 · 实时目录（数字为已读直属文件，最多预览3000项；结束后显示完整结果）", IsExpanded = true };
        public LiveScanPreview(TreeView tree)
        {
            tree.Items.Clear(); tree.Items.Add(_root);
            _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => Drain();
            _timer.Start();
        }
        public void Publish(ScanPreview value)
        {
            if (_pending.Count < 4096) _pending.Enqueue(value);
        }
        private void Drain()
        {
            for (var i = 0; i < 150 && _pending.TryDequeue(out var entry); i++)
            {
                if (!_items.TryGetValue(entry.Path, out var item))
                {
                    if (_items.Count >= 3000) continue;
                    item = new TreeViewItem(); _items.Add(entry.Path, item);
                    if (entry.ParentPath is not null && _items.TryGetValue(entry.ParentPath, out var parent)) parent.Items.Add(item);
                    else { _root.Items.Add(item); item.IsExpanded = true; }
                }
                item.Header = (string.IsNullOrEmpty(entry.Name) ? entry.Path : entry.Name)
                    + (entry.Enumerated
                        ? entry.Incomplete
                            ? entry.DirectBytes == 0 ? " · 部分未读，直属大小未知" : " · 部分未读，直属至少 " + FormatBytes(entry.DirectBytes)
                            : " · 已读直属 " + FormatBytes(entry.DirectBytes)
                        : " · 等待读取");
                item.ToolTip = entry.Path;
            }
        }
        public void Dispose() => _timer.Stop();
    }
    private sealed class ThrottledScanProgress : IProgress<ScanProgress>, IDisposable
    {
        private readonly System.Windows.Threading.DispatcherTimer _timer;
        private readonly LatestScanProgress _latest = new();
        public ThrottledScanProgress(Action<ScanProgress> update)
        {
            _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(80) };
            _timer.Tick += (_, _) =>
            {
                var value = _latest.TakeLatest();
                if (value is not null) update(value);
            };
            _timer.Start();
        }
        public void Report(ScanProgress value) => _latest.Report(value);
        public void Dispose() { _timer.Stop(); _latest.TakeLatest(); }
    }
    private static string DisplayNodeSize(ScanNode node, long minimumFileBytes) => NodeSizeStatus.Get(node) switch
    {
        NodeSizeState.SkippedLink => ScanDiagnostics.DescribeNode(node),
        NodeSizeState.Unknown => ScanDiagnostics.DescribeNode(node) is { Length: > 0 } reason ? reason : "未完成（大小未知）",
        NodeSizeState.Partial => FormatBytes(node.TotalBytes) + "（部分）",
        NodeSizeState.Known when node.IsDirectory && node.TotalBytes == 0 && minimumFileBytes > 0 => "0 B（无符合估算门槛的文件）",
        _ => FormatBytes(node.TotalBytes)
    };
    public static string FormatBytes(long bytes)
    {
        var suffixes = new[] { "B", "KiB", "MiB", "GiB", "TiB" }; var value = (double)bytes; var index = 0;
        while (value >= 1024 && index < suffixes.Length - 1) { value /= 1024; index++; }
        return $"{value:N2} {suffixes[index]}";
    }
    private sealed class NodeRow(ScanNode node, long parentBytes, int directDirectories, bool measuresAllocatedSpace, long minimumFileBytes)
    {
        public ScanNode Node => node;
        public string Name => node.Name;
        public string Path => node.Path;
        public int PathLength => node.PathLength;
        public int NameLength => node.Name.Length;
        public string Kind => node.IsDirectory ? "目录" : "文件";
        public long? SizeValue => NodeSizeStatus.SortableBytes(node);
        public string Size => DisplayNodeSize(node, minimumFileBytes);
        public string Allocated => !measuresAllocatedSpace ? "未开启统计" : node.HardLinkDuplicate ? "重复硬链接（已计入其他路径）" : node.TotalAllocatedBytes is long bytes ? FormatBytes(bytes) : "未知（未完整读取）";
        public double? PercentValue => parentBytes > 0 && SizeValue is long bytes ? 100.0 * bytes / parentBytes : null;
        public bool HasPercent => PercentValue.HasValue;
        public string Percent => PercentValue is double percent ? $"{percent:F2}%" + (node.Incomplete ? "（已读）" : "") : "—";
        public string Created => node.CreatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";
        public string Accessed => node.AccessedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";
        public string Modified => node.ModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "";
        public string Attributes => Kind;
        public long Files => node.TotalFiles;
        public int DirectDirectories => node.IsDirectory ? directDirectories : 0;
        public long Directories => node.TotalDirectories;
        public string State => ScanDiagnostics.DescribeNode(node);
    }
}
