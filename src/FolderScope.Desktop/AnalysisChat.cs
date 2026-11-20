using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FolderScope.Desktop;

internal sealed class AnalysisChat : DockPanel
{
    internal sealed record Turn(string Role, string Content);

    private readonly ModelSelector _modelSelector = new();
    private readonly TextBlock _target = new() { FontSize = 11, Foreground = Brushes.SlateGray, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { FontSize = 11, Foreground = Brushes.SlateGray, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _input = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 90,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly Button _send = new() { Content = "发送", Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _stop = new() { Content = "停止回复", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
    private readonly StackPanel _messages = new();
    private readonly ScrollViewer _transcript;
    private readonly List<Turn> _history = [];
    private (object Report, int Id, string Path)? _identity;
    private Func<string>? _material;
    private CancellationTokenSource? _pending;

    public AnalysisChat()
    {
        Margin = new Thickness(14, 10, 14, 10);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_stop);
        SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        var heading = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
        var title = new TextBlock { Text = "AI 分析", FontSize = 13, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        SetDock(title, Dock.Left);
        heading.Children.Add(title);
        heading.Children.Add(_target);
        header.Children.Add(heading);
        SetDock(header, Dock.Top);
        Children.Add(header);

        var composer = new Border { CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(221, 226, 230)), Background = Brushes.White,
            Margin = new Thickness(0, 8, 0, 2), Padding = new Thickness(10, 6, 8, 6) };
        var editor = new DockPanel();
        var composerActions = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        _send.Content = "↑";
        _send.FontSize = 17;
        _send.Width = _send.Height = 32;
        _send.MinHeight = 0;
        _send.Padding = new Thickness(0);
        _send.ToolTip = "发送消息";
        _send.Background = new SolidColorBrush(Color.FromRgb(30, 44, 52));
        _send.Foreground = Brushes.White;
        System.Windows.Automation.AutomationProperties.SetName(_send, "发送消息");
        SetDock(_send, Dock.Right);
        composerActions.Children.Add(_send);
        SetDock(_modelSelector, Dock.Right);
        composerActions.Children.Add(_modelSelector);
        composerActions.Children.Add(new TextBlock { Text = "Enter 发送 · Shift+Enter 换行", FontSize = 11,
            Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        SetDock(composerActions, Dock.Bottom);
        editor.Children.Add(composerActions);
        _input.BorderThickness = new Thickness(0);
        _input.MinHeight = 28;
        _input.Padding = new Thickness(2);
        _input.VerticalContentAlignment = VerticalAlignment.Top;
        editor.Children.Add(_input);
        composer.Child = editor;
        SetDock(composer, Dock.Bottom);
        Children.Add(composer);
        SetDock(_status, Dock.Bottom);
        Children.Add(_status);
        _transcript = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _transcript.PreviewMouseWheel += (_, e) =>
        {
            _transcript.ScrollToVerticalOffset(_transcript.VerticalOffset - e.Delta);
            e.Handled = true;
        };
        Children.Add(_transcript);
        System.Windows.Automation.AutomationProperties.SetName(_input, "AI 聊天输入");
        _input.ToolTip = "Enter 发送，Shift+Enter 换行。发送当前目标的扫描摘要和本轮对话。";
        _modelSelector.Failure += SetStatus;
        _modelSelector.DefaultChanged += settings => SetStatus("默认模型已切换 · " + settings.EffectiveProfile.Label);
        _send.Click += async (_, _) => await SendAsync(_input.Text);
        _stop.Click += (_, _) => _pending?.Cancel();
        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            await SendAsync(_input.Text);
        };
        _target.Text = "选择目录后开始分析";
        _status.Text = "发送后，模型会收到该目标的扫描摘要和对话。";
        UpdateButtons();
    }

    public void SetTarget(object report, int id, string path, Func<string> material)
    {
        if (_identity is { } current && ReferenceEquals(current.Report, report) && current.Id == id && current.Path == path) return;
        Cancel();
        _identity = (report, id, path);
        _material = material;
        _history.Clear();
        _messages.Children.Clear();
        _input.Clear();
        _target.Text = "当前目标 · " + (path == "shell:MyComputerFolder" ? "整个电脑" : path);
        _target.ToolTip = path;
        SetStatus("输入问题，分析当前目标的用途和清理候选。");
        UpdateButtons();
    }

    public void RefreshModels() => _modelSelector.RefreshModels();

    public void FocusComposer() => _input.Focus();

    public void Cancel()
    {
        var previous = _pending;
        _pending = null;
        previous?.Cancel();
    }

    private void UpdateButtons()
    {
        _modelSelector.IsEnabled = _pending is null;
        _input.IsEnabled = _material is not null;
        _send.IsEnabled = _input.IsEnabled && _pending is null;
        _stop.Visibility = _pending is null ? Visibility.Collapsed : Visibility.Visible;
        _stop.IsEnabled = _pending is not null;
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.ToolTip = text;
    }

    private void AddMessage(string text, bool fromUser)
    {
        var content = new StackPanel();
        if (fromUser)
        {
            content.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Top, MinHeight = 0 });
        }
        else
        {
            content.Children.Add(ChatMarkdown.Create(text));
            var copy = new Button { Content = "复制", ToolTip = "复制回复原文", FontSize = 11,
                MinHeight = 24, Padding = new Thickness(6, 0, 6, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.Gray };
            // Another process can hold the clipboard open; that must not surface as an unhandled
            // dispatcher exception, so report it in the status line exactly like the issue dialog does.
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(text); copy.Content = "已复制"; }
                catch (System.Runtime.InteropServices.ExternalException ex) { SetStatus("剪贴板暂不可用：" + ex.Message); }
            };
            content.Children.Add(copy);
        }
        _messages.Children.Add(new Border { Child = content,
            Background = fromUser ? new SolidColorBrush(Color.FromRgb(241, 243, 245)) : Brushes.Transparent,
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Margin = fromUser ? new Thickness(60, 10, 8, 10) : new Thickness(8, 6, 8, 12),
            Padding = fromUser ? new Thickness(14, 9, 14, 9) : new Thickness(0) });
        Dispatcher.BeginInvoke(new Action(() => _transcript.ScrollToEnd()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async Task SendAsync(string input, bool clearInput = true)
    {
        var question = input.Trim();
        if (question.Length == 0 || _material is null || _pending is not null) return;
        using var request = new CancellationTokenSource();
        _pending = request;
        UpdateButtons();
        if (clearInput) _input.Clear();
        AddMessage(question, fromUser: true);
        SetStatus("正在等待模型回复…");
        try
        {
            var settings = ModelSettings.Load();
            var context = _material();
            var userTurn = new Turn("user", question);
            var turns = _history.TakeLast(12).Append(userTurn).ToArray();
            var answer = await settings.Analyze(context, turns, request.Token);
            if (!ReferenceEquals(_pending, request)) return;
            _history.Add(userTurn);
            _history.Add(new Turn("assistant", answer));
            if (_history.Count > 12) _history.RemoveRange(0, _history.Count - 12);
            AddMessage(answer, fromUser: false);
            SetStatus("回复完成 · " + settings.Model);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_pending, request)) SetStatus(request.IsCancellationRequested ? "已停止回复。" : "模型响应超时，可重试。");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or UnauthorizedAccessException
            or InvalidOperationException or HttpRequestException or JsonException or System.ComponentModel.Win32Exception)
        {
            if (ReferenceEquals(_pending, request))
            {
                if (clearInput && _input.Text.Length == 0) _input.Text = question;
                SetStatus("分析失败：" + ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_pending, request))
            {
                _pending = null;
                UpdateButtons();
            }
        }
    }
}
