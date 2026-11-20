using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FolderScope.Desktop;

internal static class Program
{
    private static int _checks;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--local-codex-smoke"))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var profile = ModelCatalog.Presets().Single(item => item.ProviderId == "codex-cli");
            var reply = LocalCodexClient.AnalyzeAsync(profile, "Reply only OK. Do not use tools.",
                new[] { new AnalysisChat.Turn("user", "Reply OK") }, timeout.Token).GetAwaiter().GetResult();
            Check(reply.Trim() == "OK", "Local Codex real generation returns OK");
            Console.WriteLine("PASS local Codex actual application call: OK");
            return;
        }
        var folder = Path.Combine(Path.GetTempPath(), "FolderScope-model-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "models.json");
            var keyA = ModelSettings.EncryptKey("test-key-A");
            var keyB = ModelSettings.EncryptKey("test-key-B");
            var a = new ModelProfile("test:a", "test", "Test provider", "Model A", "https://unit.invalid/v1", "model-a", ProtectedKey: keyA);
            var b = new ModelProfile("test:b", "test-b", "Second provider", "Model B", "https://other.invalid/api", "model-b", "ant-messages", keyB, true);
            var initial = (new ModelSettings("https://unit.invalid/v1", "model-a", keyA, "saved prompt")).Normalize();
            var profiles = initial.ModelProfiles!.Where(item => !item.Key.StartsWith("legacy:")).Append(a).Append(b).ToList();
            initial = initial.WithProfiles(profiles, a.Key).Save(path);
            Check(!File.ReadAllText(path).Contains("test-key-"), "Keys are encrypted on disk");
            Check(ModelCatalog.Presets().Count > 50 && ModelCatalog.Presets().All(item => item.ProtectedKey == ""), "Only public presets are included");

            var legacyPath = Path.Combine(folder, "legacy.json");
            var legacyJson = JsonSerializer.Serialize(new
            {
                BaseUrl = "https://legacy.invalid/v1", Model = "legacy-model", ProtectedKey = keyA,
                AnalysisPrompt = "keep prompt", imageGeneration = new { model = "image-keep", token = "synthetic" },
                unrelated = new { untouched = true }
            });
            File.WriteAllText(legacyPath, legacyJson);
            var legacy = ModelSettings.Read(legacyPath).Save(legacyPath);
            Check(legacy.Model == "legacy-model" && ModelSettings.RevealKey(legacy.ProtectedKey) == "test-key-A"
                && legacy.EffectiveAnalysisPrompt == "keep prompt", "Legacy active model, key and prompt survive");
            using (var json = JsonDocument.Parse(File.ReadAllText(legacyPath)))
                Check(json.RootElement.GetProperty("imageGeneration").GetProperty("model").GetString() == "image-keep"
                    && json.RootElement.GetProperty("unrelated").GetProperty("untouched").GetBoolean(), "Unrelated settings and image configuration survive");

            var deepseek = new ModelSettings("https://api.deepseek.com", "deepseek-flash", keyA).Normalize();
            Check(deepseek.DefaultProfileKey == "deepseek:deepseek-v4-flash"
                && deepseek.EffectiveProfile.ProviderName == "DeepSeek"
                && deepseek.Model == "deepseek-flash"
                && !deepseek.ModelProfiles!.Any(item => item.Key == "legacy:deepseek-flash"),
                "Existing DS Flash is grouped without changing the requested model ID");
            Check(deepseek.ModelProfiles!.Where(item => item.ProviderId == "deepseek")
                .All(item => ModelSettings.RevealKey(item.ProtectedKey) == "test-key-A"),
                "All models in a provider inherit its key");
            var groupedPane = new ModelConfigurationPane(deepseek);
            var groupedList = Field<ListBox>(groupedPane, "_list");
            Check(groupedList.GroupStyle.Count == 1
                && groupedList.Items.Groups!.Count >= 12, "Catalog has collapsible provider groups");
            Field<PasswordBox>(groupedPane, "_key").Password = "provider-key-new";
            var providerSaved = groupedPane.Snapshot(deepseek).Save(path);
            Check(ModelSettings.Read(path).ModelProfiles!.Where(item => item.ProviderId == "deepseek")
                .All(item => ModelSettings.RevealKey(item.ProtectedKey) == "provider-key-new"),
                "Editing provider key persists across its models");
            Field<CheckBox>(groupedPane, "_forget").IsChecked = true;
            var clearedProvider = groupedPane.Snapshot(providerSaved).Save(path);
            Check(ModelSettings.Read(path).ModelProfiles!.Where(item => item.ProviderId == "deepseek")
                .All(item => item.ProtectedKey.Length == 0), "Provider clear removes its shared key");

            var changedB = b with { ProtectedKey = ModelSettings.EncryptKey("test-key-B-new") };
            var edited = initial.WithProfiles(initial.ModelProfiles!.Select(item => item.Key == b.Key ? changedB : item).ToList()).Save(path);
            var reopened = ModelSettings.Read(path);
            Check(reopened.DefaultProfileKey == a.Key && reopened.Model == a.Model
                && ModelSettings.RevealKey(reopened.ModelProfiles!.Single(item => item.Key == b.Key).ProtectedKey) == "test-key-B-new",
                "Editing another model keeps default and independent keys");
            var activated = reopened.WithProfiles(reopened.ModelProfiles!, b.Key).Save(path);
            activated = ModelSettings.Read(path);
            Check(activated.DefaultProfileKey == b.Key && activated.Model == b.Model && activated.ClientType == "ant-messages"
                && activated.BaseUrl == b.BaseUrl && activated.EffectiveAnalysisPrompt == "saved prompt", "Default model and protocol survive restart");

            foreach (var protocol in new[] { "openai-chat", "openai-responses", "ant-messages" })
            {
                var selected = b with { ClientType = protocol };
                var route = protocol switch { "openai-chat" => "/api/chat/completions", "openai-responses" => "/api/responses", _ => "/api/messages" };
                using var handler = new FakeHandler(request =>
                {
                    Check(request.RequestUri!.AbsolutePath == route, protocol + " endpoint");
                    Check(protocol == "ant-messages"
                        ? request.Headers.GetValues("x-api-key").Single() == "test-key-B" && request.Headers.GetValues("anthropic-version").Single() == "2023-06-01"
                        : request.Headers.Authorization?.Parameter == "test-key-B", protocol + " authentication");
                    using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    var body = json.RootElement;
                    Check(body.GetProperty("model").GetString() == "model-b", protocol + " model");
                    Check(protocol == "ant-messages" ? body.GetProperty("system").GetString() == "system test"
                        : (protocol == "openai-responses" ? body.GetProperty("input") : body.GetProperty("messages"))[0].GetProperty("role").GetString() == "system",
                        protocol + " system message");
                    return Reply(protocol);
                });
                using var http = new HttpClient(handler);
                var answer = TextModelClient.SendAsync(selected, "system test",
                    new[] { new AnalysisChat.Turn("user", "hello") }, default, http).GetAwaiter().GetResult();
                Check(answer == "OK", protocol + " visible text");
            }

            using (var handler = new FakeHandler(request =>
            {
                using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Check(request.RequestUri!.Host == "other.invalid" && request.RequestUri.AbsolutePath.EndsWith("/messages")
                    && json.RootElement.GetProperty("model").GetString() == "model-b"
                    && json.RootElement.GetProperty("system").GetString() == "saved prompt",
                    "Real Analyze entry uses saved default protocol and prompt");
                return Reply("ant-messages");
            }))
            using (var http = new HttpClient(handler))
                Check(activated.Analyze("snapshot", new[] { new AnalysisChat.Turn("user", "question") }, default, http).GetAwaiter().GetResult() == "OK",
                    "Real analysis returns adapted text");

            foreach (var response in new[]
            {
                new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("echo test-key-A") },
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") },
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") }
            })
            {
                using var handler = new FakeHandler(_ => response);
                using var http = new HttpClient(handler);
                try
                {
                    TextModelClient.SendAsync(a, "test", [], default, http).GetAwaiter().GetResult();
                    throw new Exception("Failed test reported success");
                }
                catch (InvalidOperationException ex)
                {
                    Check(!ex.Message.Contains("test-key"), "Failure is explicit and does not echo secret response");
                }
            }

            var previous = File.ReadAllText(path);
            var blocked = Path.Combine(folder, "blocked");
            Directory.CreateDirectory(blocked);
            try { activated.Save(blocked); throw new Exception("Save failure was ignored"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Check(File.ReadAllText(path) == previous, "Save failure does not corrupt prior settings"); }

            var callCount = 0;
            using var uiHandler = new FakeHandler(request =>
            {
                callCount++;
                Check(request.Headers.Authorization?.Parameter == "typed-at-last-keystroke",
                    "Immediate connection test uses newest editor key");
                return Reply("openai-chat");
            });
            using var uiHttp = new HttpClient(uiHandler);
            var pane = new ModelConfigurationPane(initial, uiHttp);
            var window = new Window { Content = pane, Width = 920, Height = 650, Resources = InterfaceTheme.Create() };
            window.Show();
            try
            {
                var keyBox = Field<PasswordBox>(pane, "_key");
                keyBox.Password = "typed-at-last-keystroke";
                Field<Button>(pane, "_test").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => Field<Button>(pane, "_test").IsEnabled);
                Check(callCount == 1 && Field<TextBlock>(pane, "_status").Text.StartsWith("连接成功"), "Latest key test succeeded in UI");
                var snap = pane.Snapshot(initial);
                Check(snap.DefaultProfileKey == a.Key && ModelSettings.RevealKey(snap.EffectiveProfile.ProtectedKey) == "typed-at-last-keystroke",
                    "UI snapshot contains immediate edits");
                var list = Field<ListBox>(pane, "_list");
                list.SelectedItem = list.Items.Cast<ModelProfile>().Single(item => item.Key == b.Key);
                Field<PasswordBox>(pane, "_key").Password = "independent-B";
                list.SelectedItem = list.Items.Cast<ModelProfile>().Single(item => item.Key == a.Key);
                var persisted = pane.Snapshot(initial).Save(path);
                Check(ModelSettings.RevealKey(persisted.ModelProfiles!.Single(item => item.Key == b.Key).ProtectedKey) == "independent-B"
                    && ModelSettings.RevealKey(persisted.EffectiveProfile.ProtectedKey) == "typed-at-last-keystroke",
                    "Switching models retains independent edits");
                // A new custom profile keeps its stable key when the model ID changes.
                var custom = new ModelProfile("custom:test-stable", "custom", "Custom", "Custom entry",
                    "https://custom.invalid/v2", "old-id", "openai-responses", ModelSettings.EncryptKey("test-custom"), true);
                var customSettings = persisted.WithProfiles(persisted.ModelProfiles!.Append(custom).ToList(), custom.Key);
                customSettings = customSettings.WithProfiles(customSettings.ModelProfiles!.Select(item =>
                    item.Key == custom.Key ? item with { Model = "new-id" } : item).ToList()).Save(path);
                var customRestored = ModelSettings.Read(path);
                Check(customRestored.DefaultProfileKey == custom.Key && customRestored.Model == "new-id"
                    && customRestored.ModelProfiles!.Count(item => item.Key == custom.Key) == 1,
                    "Custom model ID edit preserves stable identity and active config");
                var reopenedPane = new ModelConfigurationPane(customRestored, uiHttp);
                var restoredSnapshot = reopenedPane.Snapshot(customRestored);
                Check(restoredSnapshot.DefaultProfileKey == custom.Key
                    && ModelSettings.RevealKey(restoredSnapshot.EffectiveProfile.ProtectedKey) == "test-custom",
                    "Reopened settings restores custom model and protected key");

                Field<TextBox>(pane, "_query").Text = "model-b";
                Check(list.Items.Count == 1 && ((ModelProfile)list.Items[0]).Key == b.Key, "Model search filters correctly");
                Field<TextBox>(pane, "_query").Clear();
                list.SelectedItem = list.Items.Cast<ModelProfile>().Single(item => item.Key == b.Key);
                pane.SaveDefaultRequested = key =>
                {
                    var snapshot = pane.Snapshot(initial, key).Save(path);
                    pane.AcceptSaved(snapshot);
                    return true;
                };
                Field<Button>(pane, "_default").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(ModelSettings.Read(path).DefaultProfileKey == b.Key, "Set default button saves exact selected snapshot");
            }
            finally { pane.CancelTest(); window.Close(); }

            initial.Save(path);
            var selector = new ModelSelector(() => ModelSettings.Read(path), value => value.Save(path));
            selector.RefreshModels();
            Check(((ModelProfile)selector.SelectedItem).Key == a.Key, "Main selector restores default");
            selector.SelectedItem = selector.Items.Cast<ModelProfile>().Single(item => item.Key == b.Key);
            Check(ModelSettings.Read(path).DefaultProfileKey == b.Key
                && ModelSettings.Read(path).ClientType == "ant-messages", "Main selection persists actual default and protocol");
            var restartedSelector = new ModelSelector(() => ModelSettings.Read(path), value => value.Save(path));
            restartedSelector.RefreshModels();
            Check(((ModelProfile)restartedSelector.SelectedItem).Key == b.Key, "Main selection survives restart");
            string? saveFailure = null;
            var failedSelector = new ModelSelector(() => initial, _ => throw new IOException("simulated save failure"));
            failedSelector.Failure += message => saveFailure = message;
            failedSelector.RefreshModels();
            failedSelector.SelectedItem = failedSelector.Items.Cast<ModelProfile>().Single(item => item.Key == b.Key);
            Check(((ModelProfile)failedSelector.SelectedItem).Key == a.Key && saveFailure is not null,
                "Failed default save restores prior selection and reports failure");

            var statePane = new ModelConfigurationPane(deepseek);
            var stateWindow = new Window { Content = statePane, Width = 1000, Height = 720 };
            stateWindow.Show();
            try
            {
                stateWindow.UpdateLayout();
                var expanders = Visuals(statePane).OfType<Expander>().ToArray();
                var ds = expanders.Single(item => item.Header is StackPanel header
                    && ((TextBlock)header.Children[1]).Text == "DeepSeek  (3)");
                Check(((TextBlock)((StackPanel)ds.Header).Children[0]).Text == "●", "Default provider has green dot");
                Check(expanders.Where(item => !ReferenceEquals(item, ds)).All(item =>
                    ((TextBlock)((StackPanel)item.Header).Children[0]).Text == ""), "Other providers have no green dot");
                var other = expanders.First(item => item.Header is StackPanel header && ((TextBlock)header.Children[1]).Text.StartsWith("OpenRouter"));
                Check(((TextBlock)((StackPanel)other.Header).Children[1]).Foreground == System.Windows.Media.Brushes.Gray,
                    "Unconfigured provider is grey");
                ds.IsExpanded = true;
                stateWindow.UpdateLayout();
                Check(Visuals(ds).OfType<TextBlock>().Count(item => item.Text == "●") == 2,
                    "Only default model and its parent have green dots");
            }
            finally { stateWindow.Close(); }

            var providerOrder = ModelCatalog.Presets().Select(item => item.ProviderId).Distinct().ToArray();
            Check(providerOrder[0] == "deepseek" && providerOrder[1] == "codex-cli", "Local Codex is second provider");
            var local = ModelCatalog.Presets().Single(item => item.ProviderId == "codex-cli");
            if (LocalCodexClient.FindExecutable() is not null)
            {
                local.Validate(requireKey: true);
                var start = LocalCodexClient.StartInfo(local, new[] { "--version" }, folder);
                Check(start.CreateNoWindow && !start.UseShellExecute && start.RedirectStandardOutput
                    && start.RedirectStandardError && start.RedirectStandardInput,
                    "Local CLI process uses hidden redirected streams");
            }
            var proxyStart = new System.Diagnostics.ProcessStartInfo();
            foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" }) proxyStart.Environment.Remove(name);
            LocalCodexClient.ApplySystemProxy(proxyStart, true, "http=127.0.0.1:8000;https=127.0.0.1:9000");
            Check(proxyStart.Environment["HTTPS_PROXY"] == "http://127.0.0.1:9000/", "Enabled Windows HTTPS proxy inherited");
            LocalCodexClient.ApplySystemProxy(proxyStart, true, "127.0.0.1:7000");
            Check(proxyStart.Environment["HTTPS_PROXY"] == "http://127.0.0.1:9000/", "Explicit proxy is preserved");
            proxyStart.Environment.Remove("HTTP_PROXY"); proxyStart.Environment.Remove("HTTPS_PROXY");
            LocalCodexClient.ApplySystemProxy(proxyStart, false, "127.0.0.1:7000");
            Check(!proxyStart.Environment.ContainsKey("HTTPS_PROXY"), "Disabled Windows proxy not injected");
            Check(LocalCodexClient.FailureMessage(1, "model_not_found SECRET") .Contains("模型")
                && !LocalCodexClient.FailureMessage(1, "model_not_found SECRET").Contains("SECRET"), "Model failure is classified without raw diagnostics");
            Check(LocalCodexClient.FailureMessage(1, "os error 10061").Contains("代理"), "Network failure has actionable message");
            Check(LocalCodexClient.FailureMessage(1, "429 quota").Contains("额度"), "Quota failure has actionable message");
            var rowType = typeof(ScanWindow).GetNestedType("NodeRow", BindingFlags.NonPublic)!;
            (double? Value, string Text) Percentage(FolderScope.Core.ScanNode node)
            {
                var row = Activator.CreateInstance(rowType, node, 100L, 0, false, 0L)!;
                return ((double?)rowType.GetProperty("PercentValue")!.GetValue(row),
                    (string)rowType.GetProperty("Percent")!.GetValue(row)!);
            }
            var unknownPercent = Percentage(new FolderScope.Core.ScanNode { Name = "unknown", Path = "unknown", Incomplete = true });
            var zeroPercent = Percentage(new FolderScope.Core.ScanNode { Name = "empty", Path = "empty" });
            var partialPercent = Percentage(new FolderScope.Core.ScanNode { Name = "partial", Path = "partial", TotalBytes = 20, Incomplete = true });
            Check(unknownPercent.Value is null && unknownPercent.Text == "—" && zeroPercent.Value == 0
                && partialPercent.Value == 20 && partialPercent.Text.EndsWith("（已读）"), "Percentages distinguish unknown, real zero and partial values");
            // A corrupt settings file must degrade to a status message. DesktopSettings.Load reports
            // invalid bounds as InvalidDataException, which is not an IOException, so the window
            // constructor has to catch it or the application dies before it can show anything.
            var desktopSettingsPath = FolderScope.Desktop.DesktopSettings.FilePath;
            var desktopSettingsBackup = File.Exists(desktopSettingsPath) ? File.ReadAllBytes(desktopSettingsPath) : null;
            try
            {
                File.WriteAllText(desktopSettingsPath, "{\"MemoryBudgetGiB\":3}");
                var corruptSettingsWindow = new ScanWindow(null);
                try
                {
                    Check(Field<TextBlock>(corruptSettingsWindow, "_status").Text.StartsWith("设置未恢复"),
                        "Corrupt settings degrade to a status message instead of aborting startup");
                }
                finally { corruptSettingsWindow.Close(); }
            }
            finally
            {
                if (desktopSettingsBackup is null) File.Delete(desktopSettingsPath);
                else File.WriteAllBytes(desktopSettingsPath, desktopSettingsBackup);
            }
            var tinyScanRoot = Path.Combine(folder, "single-target");
            Directory.CreateDirectory(tinyScanRoot);
            var scopeWindow = new ScanWindow(null);
            try
            {
                Check(Field<TextBox>(scopeWindow, "_path").Height == 28 && Field<TextBox>(scopeWindow, "_searchInput").Height == 28,
                    "Main command rows use compact uniform control heights");
                var historySelector = Field<Button>(scopeWindow, "_historySelector");
                var tree = Field<TreeView>(scopeWindow, "_tree");
                Check(historySelector.Parent is DockPanel historyPane && ReferenceEquals(tree.Parent, historyPane)
                    && DockPanel.GetDock(historySelector) == Dock.Top, "History selector sits directly above directory tree");
                var scope = Field<ComboBox>(scopeWindow, "_scanScope");
                scope.SelectedItem = "D:\\";
                Check(Field<string>(scopeWindow, "_scanTarget") == "D:\\", "Single drive selection sets authoritative scan target");
                Field<TextBox>(scopeWindow, "_path").Text = FolderScope.Core.ComputerScanner.SelectionName;
                Check(Field<string>(scopeWindow, "_scanTarget") == "D:\\", "Snapshot display cannot change selected drive");
                var scan = new FolderScope.Core.DirectoryScanner().Scan(tinyScanRoot);
                var snapshot = Path.Combine(folder, "scope-snapshot.json");
                FolderScope.Core.ReportWriter.Write(scan, snapshot);
                var oldContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var load = (Task)typeof(ScanWindow).GetMethod("LoadReport", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(scopeWindow, new object?[] { snapshot, false })!;
                PumpUntil(() => load.IsCompleted);
                load.GetAwaiter().GetResult();
                Check(Field<TextBlock>(scopeWindow, "_historyLabel").Text.Contains(tinyScanRoot), "History selector displays loaded record target");
                if (args.Contains("--layout-preview"))
                {
                    var content = (FrameworkElement)scopeWindow.Content;
                    content.Measure(new Size(1120, 680)); content.Arrange(new Rect(0, 0, 1120, 680)); content.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1120, 680, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    var background = new System.Windows.Media.DrawingVisual();
                    using (var drawing = background.RenderOpen()) drawing.DrawRectangle(scopeWindow.Background, null, new Rect(0, 0, 1120, 680));
                    bitmap.Render(background); bitmap.Render(content);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var image = File.Create(Path.Combine(Environment.CurrentDirectory, "research", "layout-2026.11.14.12.png"));
                    png.Save(image);
                }
                SynchronizationContext.SetSynchronizationContext(oldContext);
                Check(Field<string>(scopeWindow, "_scanTarget") == "D:\\" && Field<TextBox>(scopeWindow, "_path").Text == "D:\\",
                    "Loading a different target snapshot preserves single-drive scan choice");
                var entry = ScanHistoryEntry.Read(snapshot);
                Check(entry.Target == tinyScanRoot && entry.FinishedUtc == scan.FinishedUtc,
                    "History label reads envelope without loading nodes");
                File.Delete(snapshot);
            }
            finally
            {
                scopeWindow.Close();
                Directory.Delete(tinyScanRoot);
            }


            Console.WriteLine($"PASS {_checks} model configuration checks. Mock requests only.");
        }
        finally
        {
            // Remove only exact, known files created by this test in its unique temporary directory.
            foreach (var name in new[] { "models.json", "legacy.json" })
            {
                var file = Path.Combine(folder, name);
                if (File.Exists(file)) File.Delete(file);
            }
            var blocked = Path.Combine(folder, "blocked");
            if (Directory.Exists(blocked) && !Directory.EnumerateFileSystemEntries(blocked).Any()) Directory.Delete(blocked);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Visuals(child)) yield return descendant;
        }
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL " + label);
        _checks++;
    }

    private static void PumpUntil(Func<bool> done)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed.TotalSeconds > 6) throw new Exception("Timed out waiting for UI");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static HttpResponseMessage Reply(string protocol) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(protocol switch
        {
            "ant-messages" => """{"type":"message","content":[{"type":"text","text":"OK"}]}""",
            "openai-responses" => """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"OK"}]}]}""",
            _ => """{"choices":[{"message":{"content":"OK"}}]}"""
        }, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(reply(request));
        }
    }
}
