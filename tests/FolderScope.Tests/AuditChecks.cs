using FolderScope.Core;
using System.Text.Json;

internal static class AuditChecks
{
    private sealed class RejectProgress : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => throw new InvalidOperationException("A drive was unexpectedly queued");
    }

    private sealed class CancelQueuedDrive(CancellationTokenSource stop) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => stop.Cancel();
    }

    public static void Run()
    {
        var project = ResolveProjectRoot();
        var research = Path.Combine(project, "research");
        var scratch = Path.Combine(research, "audit-tiny-" + Guid.NewGuid().ToString("N"));
        if (!string.Equals(Path.GetDirectoryName(scratch), research, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("审计临时目录必须是 <项目根>/research 的直接子目录。");
        if (Directory.Exists(research) && (File.GetAttributes(research) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("<项目根>/research 不能是符号链接或重解析点。");
        Directory.CreateDirectory(scratch);
        var failures = new List<string>();
        var count = 0;
        void Check(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); count++; }
            catch (Exception ex) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + " " + ex.Message); }
        }
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        try
        {
            var fixture = Path.Combine(scratch, "fixture");
            Directory.CreateDirectory(Path.Combine(fixture, "child"));
            File.WriteAllBytes(Path.Combine(fixture, "first.bin"), new byte[7]);
            File.WriteAllBytes(Path.Combine(fixture, "empty.bin"), []);
            File.WriteAllBytes(Path.Combine(fixture, "child", "second.bin"), new byte[5]);
            Check("allocated skipped-link records remain readable in page and ranking", () =>
            {
                var path = Path.Combine(scratch, "skipped.jsonl");
                using (var store = new FileDetailStore(path)) store.Append(new ScanNode
                { Id = 1, ParentId = 0, Path = Path.Combine(fixture, "link"), Name = "link", LinkSkipped = true,
                    OwnAllocatedBytes = 0, TotalAllocatedBytes = null, UnlistedAllocatedBytes = 0 });
                var page = FileDetailStore.ReadPage(path, 0);
                var top = FileDetailStore.ReadLargest(path, 0);
                Require(page.Files.Count == 1 && top.Files.Count == 1 && page.Files[0].LinkSkipped && top.Files[0].TotalAllocatedBytes is null,
                    "Skipped link must retain unknown allocation");
            });
            Check("cancelled scan preserves browsable pages without an index", () =>
            {
                using var stop = new CancellationTokenSource();
                IEnumerable<FileSystemInfo> Enumerate(DirectoryInfo dir, EnumerationOptions options)
                {
                    yield return new FileInfo(Path.Combine(dir.FullName, "first.bin"));
                    yield return new FileInfo(Path.Combine(dir.FullName, "empty.bin"));
                    stop.Cancel();
                }
                var path = Path.Combine(scratch, "cancelled-pages.jsonl");
                ScanReport report;
                using (var store = new FileDetailStore(path))
                {
                    report = new DirectoryScanner(Enumerate).Scan(fixture, stop.Token, retainFileNodes: false, fileDetails: store);
                    store.Finish(writeIndex: !stop.IsCancellationRequested);
                }
                Require(report.Cancelled && !report.Complete && !File.Exists(path + ".index.json"), "Unexpected completion or index");
                var first = FileDetailStore.ReadPage(path, 0, 0, 1, expectedStoreId: report.FileDetailsStoreId);
                var last = FileDetailStore.ReadPage(path, 0, 1, 1, expectedStoreId: report.FileDetailsStoreId);
                Require(first.HasMore && !last.HasMore && first.Files.Single().Name == "first.bin" &&
                    last.Files.Single().Name == "empty.bin" && last.Files.Single().TotalBytes == 0, "Partial pages lost or duplicated");
            });
            Check("computer root exhausts its node budget before any drive is queued", () =>
            {
                var report = ComputerScanner.Scan(maximumNodes: 1, measureAllocatedSpace: true,
                    progress: new RejectProgress());
                Require(report.Nodes.Count == 1 && !report.Complete && !report.Cancelled
                    && report.Root.TotalAllocatedBytes is null
                    && (report.Root.IncompleteReasons & ScanIncompleteReason.NodeLimit) != 0,
                    "Exhausted global node budget did not produce an incomplete computer root");
                var snapshot = Path.Combine(scratch, "computer-root-limit.json");
                ReportWriter.Write(report, snapshot);
                Require(!ReportReader.Read(snapshot).Complete, "Partial computer root did not round trip");
            });
            Check("pre-cancelled computer scan preserves unknown allocation after reload", () =>
            {
                using var stop = new CancellationTokenSource();
                stop.Cancel();
                var report = ComputerScanner.Scan(stop.Token, progress: new RejectProgress(), measureAllocatedSpace: true);
                var snapshot = Path.Combine(scratch, "cancelled-computer.json");
                ReportWriter.Write(report, snapshot);
                var restored = ReportReader.Read(snapshot);
                Require(restored.Cancelled && !restored.Complete && restored.Nodes.Count == 1
                    && restored.Root.TotalAllocatedBytes is null
                    && (restored.Root.IncompleteReasons & ScanIncompleteReason.Cancelled) != 0,
                    "Pre-cancelled computer root lost its cancellation or unknown allocation");
            });
            Check("cancelling as a drive queues stops before any drive is opened", () =>
            {
                using var stop = new CancellationTokenSource();
                var report = ComputerScanner.Scan(stop.Token, progress: new CancelQueuedDrive(stop),
                    measureAllocatedSpace: true);
                Require(stop.IsCancellationRequested && report.Cancelled && report.Nodes.Count == 1
                    && report.Root.TotalAllocatedBytes is null, "Queued drive was opened after cancellation");
            });
            Check("multi-root merge preserves totals, detail ownership and cancellation after snapshot reload", () =>
            {
                var other = Path.Combine(scratch, "second-root");
                Directory.CreateDirectory(other);
                var secondPath = Path.Combine(other, "other.bin");
                File.WriteAllBytes(secondPath, new byte[3]);
                foreach (var cancelSecond in new[] { false, true })
                {
                    using var stop = new CancellationTokenSource();
                    IEnumerable<FileSystemInfo> Enumerate(DirectoryInfo dir, EnumerationOptions options)
                    {
                        yield return new FileInfo(secondPath);
                        if (cancelSecond) stop.Cancel();
                    }

                    var path = Path.Combine(scratch, $"merged-{cancelSecond}.jsonl");
                    ScanReport merged;
                    using (var store = new FileDetailStore(path))
                    {
                        merged = new ScanReport { RootPath = ComputerScanner.SelectionName, RetainsFileNodes = false,
                            FileDetailsPath = path, FileDetailsStoreId = store.StoreId, StartedUtc = DateTime.UtcNow,
                            MeasuresAllocatedSpace = true, AllocationTotalsVersion = 1 };
                        merged.Nodes.Add(new ScanNode { Id = 0, Path = "shell:MyComputerFolder",
                            Name = ComputerScanner.SelectionName, IsDirectory = true,
                            OwnAllocatedBytes = 0, UnlistedAllocatedBytes = 0, TotalAllocatedBytes = 0 });
                        foreach (var (scanner, root) in new[] { (new DirectoryScanner(), fixture), (new DirectoryScanner(Enumerate), other) })
                        {
                            var scanned = scanner.Scan(root, stop.Token, retainFileNodes: false, fileDetails: store,
                                measureAllocatedSpace: true, fileDetailNodeIdOffset: merged.Nodes.Count);
                            ComputerScanner.MergeDrive(merged, scanned, Path.GetFileName(root));
                        }
                        merged.FinishedUtc = DateTime.UtcNow;
                        store.Finish(writeIndex: !stop.IsCancellationRequested);
                    }
                    var snapshot = Path.Combine(scratch, $"merged-{cancelSecond}.json");
                    ReportWriter.Write(merged, snapshot);
                    var restored = ReportReader.Read(snapshot);
                    Require(restored.Cancelled == cancelSecond && restored.Complete != cancelSecond
                        && restored.Root.TotalBytes == 15 && restored.Root.TotalFiles == 4
                        && restored.Root.TotalDirectories == 3, "Combined report lost totals or cancellation");
                    var first = restored.Nodes.Single(n => n.Path == fixture);
                    var second = restored.Nodes.Single(n => n.Path == other);
                    var child = restored.Nodes.Single(n => n.Path == Path.Combine(fixture, "child"));
                    Require(first.ParentId == 0 && second.ParentId == 0 && child.ParentId == first.Id
                        && !first.Incomplete && second.Incomplete == cancelSecond,
                        "Root or descendant relationships changed");
                    Require(first.TotalAllocatedBytes.HasValue && (cancelSecond
                        ? restored.Root.TotalAllocatedBytes is null && second.TotalAllocatedBytes is null
                        : second.TotalAllocatedBytes.HasValue &&
                            restored.Root.TotalAllocatedBytes == first.TotalAllocatedBytes + second.TotalAllocatedBytes),
                        "Allocated totals or unknown state were lost");
                    Require(FileDetailStore.ReadPage(path, first.Id, expectedStoreId: restored.FileDetailsStoreId).Files.Count == 2
                        && FileDetailStore.ReadPage(path, child.Id, expectedStoreId: restored.FileDetailsStoreId).Files.Single().TotalBytes == 5
                        && FileDetailStore.ReadPage(path, second.Id, expectedStoreId: restored.FileDetailsStoreId).Files.Single().Path == secondPath,
                        "A file was lost or assigned to the wrong root");
                }
            });
            Check("detail index mid-sort cancellation reaches caller", () =>
            {
                var method = typeof(FileDetailStore).GetMethod("SortWithCancellation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MakeGenericMethod(typeof(int));
                foreach (var asArray in new[] { true, false })
                {
                    using var cancellation = new CancellationTokenSource();
                    var input = Enumerable.Range(0, 4096).Reverse().ToArray();
                    IList<int> values = asArray ? input : input.ToList();
                    var comparisons = 0;
                    Comparison<int> compare = (a, b) => { if (++comparisons == 1) cancellation.Cancel(); return a.CompareTo(b); };
                    var cancelled = false;
                    try { method.Invoke(null, new object[] { values, compare, cancellation.Token }); }
                    catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is OperationCanceledException oce)
                    { cancelled = oce.CancellationToken == cancellation.Token; }
                    Require(cancelled && comparisons > 0 && comparisons <= 1024, "Mid-sort cancellation was lost or delayed");
                }
            });
            Check("offline skipped item is distinguished from a normal link and true zero", () =>
            {
                var offline = new ScanNode { Name = "offline", Path = "offline", LinkSkipped = true,
                    Attributes = (int)(FileAttributes.Offline | FileAttributes.ReparsePoint) };
                var link = new ScanNode { Name = "link", Path = "link", LinkSkipped = true,
                    Attributes = (int)FileAttributes.ReparsePoint };
                Require(ScanDiagnostics.DescribeNode(offline).Contains("按需取回")
                    && !ScanDiagnostics.DescribeNode(link).Contains("按需取回")
                    && NodeSizeStatus.SortableBytes(offline) is null,
                    "Offline skipped size must remain unknown");
            });
            Check("size bounds distinguish zero unknown and partial totals", () =>
            {
                var empty = new ScanNode { Name = "empty", Path = "empty", TotalBytes = 0 };
                var unknown = new ScanNode { Name = "unknown", Path = "unknown", TotalBytes = 0, Incomplete = true };
                var partial = new ScanNode { Name = "partial", Path = "partial", TotalBytes = 1024, Incomplete = true };
                var skipped = new ScanNode { Name = "skipped", Path = "skipped", LinkSkipped = true };
                var minimum = NodeFilter.Create(new NodeFilterOptions { MinimumBytes = 0 });
                Require(minimum(empty) && !minimum(unknown) && !minimum(skipped), "Unknown size passed a zero minimum");
                var boundary = NodeFilter.Create(new NodeFilterOptions { MinimumBytes = 1024 });
                Require(boundary(partial), "Known lower bound failed inclusive minimum");
                var maximum = NodeFilter.Create(new NodeFilterOptions { MaximumBytes = 2048 });
                Require(!maximum(partial) && maximum(empty), "Partial size was treated as a known upper bound");
                var excluded = NodeFilter.Create(new NodeFilterOptions { MinimumBytes = 2048, ExcludeMatches = true });
                Require(!excluded(partial) && !excluded(unknown) && excluded(empty), "Exclusion misclassified uncertain sizes");
                Require(NodeFilter.Create(new NodeFilterOptions())(unknown), "Unbounded browsing hid unknown nodes");
            });
            Check("size ranking places true zero before unknown in both query paths", () =>
            {
                var report = new ScanReport { RootPath = "root", Nodes =
                [
                    new() { Name = "root", Path = "root", IsDirectory = true },
                    new() { Name = "a-unknown", Path = "a-unknown", Incomplete = true },
                    new() { Name = "z-empty", Path = "z-empty" },
                    new() { Name = "partial", Path = "partial", TotalBytes = 7, Incomplete = true },
                    new() { Name = "full", Path = "full", TotalBytes = 12 }
                ] };
                var expected = new[] { "full", "partial", "z-empty", "a-unknown" };
                Require(ReportQuery.Select(report, ReportScope.Files, ReportOrder.Largest).Select(n => n.Name).SequenceEqual(expected), "Full query conflated unknown and zero");
                foreach (var limit in new[] { 1, 3, 4, 10 })
                    Require(ReportQuery.SelectTop(report, ReportScope.Files, ReportOrder.Largest, limit).Select(n => n.Name).SequenceEqual(expected.Take(limit)), "Bounded query conflated unknown and zero");
            });
            Check("incomplete zero totals stay unknown after empty files were counted", () =>
            {
                var node = new ScanNode { Name = "dir", Path = "dir", IsDirectory = true,
                    TotalFiles = 2, TotalBytes = 0, Incomplete = true,
                    IncompleteReasons = ScanIncompleteReason.Cancelled };
                Require(NodeSizeStatus.Get(node) == NodeSizeState.Unknown, "Incomplete zero was classified as known");
                var description = ScanDiagnostics.DescribeNode(node);
                Require(description.Contains("大小未知") && description.Contains("扫描已取消"), "Empty files hid unknown size or cancellation reason");
                node.Incomplete = false;
                node.IncompleteReasons = ScanIncompleteReason.None;
                Require(NodeSizeStatus.Get(node) == NodeSizeState.Known && ScanDiagnostics.DescribeNode(node) == "", "Complete zero was labelled unknown");
            });
            Check("logical byte text distinguishes exact zero lower bound and unknown", () =>
            {
                var node = new ScanNode { Name = "test", Path = "test" };
                Require(NodeSizeStatus.DescribeLogicalBytes(node) == "0", "True zero lost");
                node.Incomplete = true;
                Require(NodeSizeStatus.DescribeLogicalBytes(node) == "unknown", "Unknown printed as zero");
                node.TotalBytes = 7;
                Require(NodeSizeStatus.DescribeLogicalBytes(node) == ">= 7 (partial)", "Partial printed as exact");
                node.LinkSkipped = true;
                Require(NodeSizeStatus.DescribeLogicalBytes(node) == "unknown (skipped)", "Skipped record printed as measured");
            });
            Check("pre-cancelled snapshot write creates no output", () =>
            {
                using var stop = new CancellationTokenSource();
                stop.Cancel();
                var output = Path.Combine(scratch, "cancelled-export.json");
                var rejected = false;
                try { ReportWriter.Write(new DirectoryScanner().Scan(fixture), output, stop.Token); }
                catch (OperationCanceledException) { rejected = true; }
                Require(rejected && !File.Exists(output)
                    && !Directory.EnumerateFiles(scratch, ".folderscope-*.tmp").Any(),
                    "Cancelled write created a committed or temporary snapshot");
            });
            Check("snapshot output rejects each write path after cancellation", () =>
            {
                using var stop = new CancellationTokenSource();
                var output = Path.Combine(scratch, "cancelled-stream.tmp");
                using (var stream = new ReportWriter.CancellableOutputStream(output, stop.Token))
                {
                    stream.WriteByte(7);
                    stop.Cancel();
                    var rejected = 0;
                    try { stream.WriteByte(8); } catch (OperationCanceledException) { rejected++; }
                    try { stream.Write(new byte[] { 9 }, 0, 1); } catch (OperationCanceledException) { rejected++; }
                    try { stream.Write(new byte[] { 10 }.AsSpan()); } catch (OperationCanceledException) { rejected++; }
                    Require(rejected == 3, "A snapshot write path ignored cancellation");
                }
                Require(File.ReadAllBytes(output).SequenceEqual(new byte[] { 7 }), "Cancelled stream wrote additional data");
            });

            Check("cancelled snapshot load does not open source", () =>
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var cancelled = false;
                try { ReportReader.Read(Path.Combine(scratch, "missing.json"), cancellationToken: cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled, "Load opened the source despite cancellation");
            });
            Check("snapshot budget rejects before opening source", () =>
            {
                var rejected = false;
                try { ReportReader.Read(Path.Combine(scratch, "missing-budget.json"), maximumManagedBytes: 1); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "Memory budget was not checked before opening source");
            });
            Check("both allocation readers reject offline files", () =>
            {
                var file = Path.Combine(fixture, "first.bin");
                var original = File.GetAttributes(file);
                try
                {
                    File.SetAttributes(file, original | FileAttributes.Offline);
                    Require((File.GetAttributes(file) & FileAttributes.Offline) != 0, "Offline attribute was not applied");
                    var modern = false; var legacy = false;
                    try { FileSpaceReader.Read(file); } catch (IOException ex) { modern = ex.Message.Contains("recall-marked"); }
                    try { AllocatedSpace.ReadFile(file); } catch (IOException ex) { legacy = ex.Message.Contains("recall-marked"); }
                    Require(modern && legacy, "An allocation reader bypassed offline protection");
                    var page = DirectoryFileReader.Read(fixture, minimumFileBytes: 6);
                    var listed = page.Files.Single();
                    Require(listed.Name == "first.bin" && listed.TotalBytes == 7 && listed.TotalAllocatedBytes is null
                        && FileSpaceReader.MayRecallData((FileAttributes)listed.Attributes),
                        "Live file browsing confused the logical length with unavailable allocation");
                    Require(DirectoryFileReader.Read(fixture, minimumFileBytes: 7).Files.Count == 0,
                        "Live file browsing allowed an offline file exactly at the threshold");
                }
                finally { File.SetAttributes(file, original); }
            });
            Check("offline allocation uncertainty preserves logical totals across all detail modes", () =>
            {
                var file = Path.Combine(fixture, "first.bin");
                var attributes = File.GetAttributes(file);
                try
                {
                    File.SetAttributes(file, attributes | FileAttributes.Offline);
                    for (var mode = 0; mode < 3; mode++)
                    {
                        var details = mode == 2 ? new FileDetailStore(Path.Combine(scratch, "offline-details.jsonl")) : null;
                        using (details)
                        {
                            var report = new DirectoryScanner().Scan(fixture, retainFileNodes: mode == 0,
                                measureAllocatedSpace: true, fileDetails: details);
                            details?.Finish(writeIndex: true);
                            Require(report.Root.TotalBytes == 12 && report.Root.TotalFiles == 3
                                && report.Root.TotalAllocatedBytes is null && !report.Complete
                                && !report.Root.Incomplete, "Offline allocation failure changed logical completeness or totals");
                            Require(report.Issues.Count == 1 && report.Issues[0].Operation == "allocated-space",
                                "Offline allocation issue was lost or duplicated");
                            if (mode == 0 || details is not null)
                            {
                                var nodes = mode == 0 ? report.Nodes : FileDetailStore.ReadAllFiles(details!.FilePath, report.FileDetailsStoreId).ToList();
                                var entry = nodes.Single(node => node.Name == "first.bin");
                                Require(entry.TotalBytes == 7 && entry.TotalFiles == 1 && entry.TotalAllocatedBytes is null
                                    && NodeSizeStatus.Get(entry) == NodeSizeState.Known,
                                    "Known logical size was confused with unknown allocation");
                            }
                        }
                    }
                }
                finally { File.SetAttributes(file, attributes); }
            });

            Check("file changing to directory is a recoverable metadata error", () =>
            {
                var rejectedAsIo = false;
                try { FileSpaceReader.Read(fixture); } catch (IOException) { rejectedAsIo = true; }
                Require(rejectedAsIo, "Changed filesystem type escaped the recoverable error boundary");
            });
            Check("scanner rejects its own growing detail output before enumeration", () =>
            {
                using var store = new FileDetailStore(Path.Combine(fixture, "inside.jsonl"));
                var rejected = false;
                try { new DirectoryScanner().Scan(fixture, maximumNodes: 1, retainFileNodes: false, fileDetails: store); }
                catch (ArgumentException) { rejected = true; }
                Require(rejected, "Scanner accepted file details inside scan input");
            });
            // Remove only the two exact files created by the previous probe, before total checks.
            File.Delete(Path.Combine(fixture, "inside.jsonl"));
            File.Delete(Path.Combine(fixture, "inside.jsonl.index.json"));
            Check("logical and allocated totals agree across memory, compact and disk modes", () =>
            {
                var fakeIdentity = new FileIdentity(1, 2, 3);
                FileSpaceInfo Space(string path) => path.EndsWith("empty.bin") ? new(0, 0, 1) : new(new FileInfo(path).Length, 4096, 2, fakeIdentity);
                for (var mode = 0; mode < 3; mode++)
                {
                    using var details = mode == 2 ? new FileDetailStore(Path.Combine(scratch, "valid.jsonl")) : null;
                    var report = new DirectoryScanner(Space).Scan(fixture, retainFileNodes: mode == 0, measureAllocatedSpace: true, fileDetails: details);
                    details?.Dispose();
                    Require(report.Complete && report.Root.TotalBytes == 12 && report.Root.TotalFiles == 3 && report.Root.TotalDirectories == 1
                        && report.Root.TotalAllocatedBytes == 4096 && report.DuplicateHardLinkPaths == 1, "Mode disagreement " + mode);
                    var path = Path.Combine(scratch, "mode-" + mode + ".json");
                    ReportWriter.Write(report, path);
                    var restored = ReportReader.Read(path);
                    Require(restored.Root.TotalBytes == 12 && restored.Root.TotalAllocatedBytes == 4096, "Snapshot mismatch");
                }
            });
            Check("index cancellation aborts loading but sort cancellation preserves scan entries", () =>
            {
                var report = new DirectoryScanner().Scan(fixture);
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var cancelled = false;
                try { DirectoryIndex.Create(report, cancellationToken: cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled, "Index load ignored cancellation");
                var budgetRejected = false;
                try { DirectoryIndex.Create(report, maximumManagedBytes: 1); }
                catch (InvalidDataException) { budgetRejected = true; }
                Require(budgetRejected, "Index ignored its memory budget");
                var partialSort = DirectoryIndex.Create(report, sortCancellationToken: cancellation.Token);
                Require(partialSort.SortingCancelled && partialSort.Children.Values.Sum(v => v.Count) == report.Nodes.Count - 1,
                    "Stopping scan sorting discarded entries");
            });
            Check("allocation failures preserve logical counts and leave allocation unknown", () =>
            {
                foreach (var retain in new[] { true, false })
                foreach (var error in new Exception[] { new UnauthorizedAccessException("audit"), new InvalidDataException("audit") })
                {
                    var report = new DirectoryScanner(_ => throw error).Scan(fixture, retainFileNodes: retain, measureAllocatedSpace: true);
                    var expectedErrorCode = error is UnauthorizedAccessException ? (int?)5 : null;
                    Require(!report.Complete && report.Root.TotalBytes == 12 && report.Root.TotalFiles == 3
                        && !report.Root.Incomplete && report.Root.TotalAllocatedBytes is null && report.Issues.Count == 3
                        && report.Issues.All(i => i.ErrorCode == expectedErrorCode && i.Operation == "allocated-space"),
                        "Allocation failure lost logical size or produced a fabricated allocation");
                }
            });
            Check("invalid allocation metadata preserves disk detail after snapshot reload", () =>
            {
                var details = Path.Combine(scratch, "allocation-invalid.jsonl");
                var snapshot = Path.Combine(scratch, "allocation-invalid.json");
                var scanner = new DirectoryScanner(_ => throw new InvalidDataException("audit"));
                ScanReport report;
                using (var store = new FileDetailStore(details))
                {
                    report = scanner.Scan(fixture, retainFileNodes: false, measureAllocatedSpace: true, fileDetails: store);
                    store.Finish(writeIndex: true);
                }
                ReportWriter.WriteHistorySnapshot(report, snapshot);
                var restored = ReportReader.Read(snapshot);
                var files = FileDetailStore.ReadAllFiles(details, restored.FileDetailsStoreId).ToArray();
                Require(!restored.RetainsFileNodes && !restored.Complete && restored.Root.TotalFiles == 3
                    && restored.Root.TotalBytes == 12 && restored.Root.TotalAllocatedBytes is null
                    && files.Length == 3 && files.Sum(file => file.TotalBytes) == 12
                    && files.All(file => file.TotalAllocatedBytes is null && NodeSizeStatus.Get(file) == NodeSizeState.Known),
                    "Disk details lost logical size or turned unknown allocation into zero");
            });
            Check("pre-cancel and safety limits cannot claim a complete zero result", () =>
            {
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                var report = new DirectoryScanner().Scan(fixture, cancelled.Token);
                Require(report.Cancelled && !report.Complete && NodeSizeStatus.Get(report.Root) == NodeSizeState.Unknown && report.Root.IncompleteReasons.HasFlag(ScanIncompleteReason.Cancelled), "Cancelled scan appears empty");
                var limited = new DirectoryScanner().Scan(fixture, maximumNodes: 1, measureAllocatedSpace: true);
                Require(!limited.Complete && limited.Root.Incomplete && limited.Root.TotalAllocatedBytes is null && limited.Issues.Any(x => x.Operation == "node-limit") && limited.Root.IncompleteReasons.HasFlag(ScanIncompleteReason.NodeLimit), "Limit appears complete");
            });
            Check("mid-scan node limits preserve parent sums and incomplete reasons", () =>
            {
                foreach (var limit in new[] { 2, 3, 4 })
                {
                    var report = new DirectoryScanner().Scan(fixture, maximumNodes: limit, measureAllocatedSpace: true);
                    Require(!report.Complete && report.Root.IncompleteReasons.HasFlag(ScanIncompleteReason.NodeLimit), "Mid-scan limit appears complete");
                    foreach (var parent in report.Nodes.Where(n => n.IsDirectory))
                    {
                        var children = report.Nodes.Where(n => n.ParentId == parent.Id).ToArray();
                        Require(parent.TotalBytes == parent.OwnBytes + parent.UnlistedFileBytes + children.Sum(n => n.TotalBytes), "Parent lost or duplicated known bytes");
                        foreach (var child in children.Where(n => n.Incomplete))
                            Require(parent.Incomplete && (parent.IncompleteReasons & child.IncompleteReasons) == child.IncompleteReasons, "Parent lost child failure reason");
                        if (parent.Incomplete)
                            Require(parent.TotalAllocatedBytes is null && NodeSizeStatus.Get(parent) != NodeSizeState.Known, "Incomplete parent claimed known totals");
                    }
                }
            });
            Check("cancel during final enumeration does not report a complete scan", () =>
            {
                using var stop = new CancellationTokenSource();
                IEnumerable<FileSystemInfo> Enumerate(DirectoryInfo directory, EnumerationOptions options)
                {
                    yield return new FileInfo(Path.Combine(directory.FullName, "first.bin"));
                    stop.Cancel();
                }
                var report = new DirectoryScanner(Enumerate).Scan(fixture, stop.Token);
                Require(report.Cancelled && !report.Complete && report.Root.TotalBytes == 7,
                    "Late cancellation lost or discarded previously read bytes");
            });
            Check("child enumeration failure preserves bytes and propagates to root", () =>
            {
                IEnumerable<FileSystemInfo> Enumerate(DirectoryInfo directory, EnumerationOptions options)
                {
                    foreach (var entry in directory.EnumerateFileSystemInfos("*", options)) yield return entry;
                    if (directory.Name == "child") throw new IOException("Injected enumeration failure");
                }
                foreach (var retain in new[] { true, false })
                {
                    var updates = new List<ScanPreview>();
                    var report = new DirectoryScanner(Enumerate).Scan(fixture, retainFileNodes: retain, preview: updates.Add);
                    var child = report.Nodes.Single(n => n.Name == "child");
                    var preview = updates.Last(update => update.Path == child.Path);
                    Require(preview.Enumerated && preview.Incomplete && preview.DirectBytes == 5,
                        "Live preview lost the enumeration failure or the bytes already read");
                    Require(child.TotalBytes == 5 && report.Root.TotalBytes == 12 && !report.Complete, "Lost known bytes after enumeration failed");
                    Require(new[] { child, report.Root }.All(n => NodeSizeStatus.Get(n) == NodeSizeState.Partial && n.IncompleteReasons.HasFlag(ScanIncompleteReason.ReadError)), "Lost partial state or error reason");
                    Require(report.Issues.Any(i => i.Path == child.Path && i.Operation == "enumerate"), "Lost failing directory");
                }
                var detailPath = Path.Combine(scratch, "partial-enumeration.jsonl");
                ScanReport stored;
                using (var store = new FileDetailStore(detailPath))
                    stored = new DirectoryScanner(Enumerate).Scan(fixture, retainFileNodes: false, fileDetails: store);
                var folder = stored.Nodes.Single(n => n.Name == "child");
                var files = FileDetailStore.ReadPage(detailPath, folder.Id, expectedStoreId: stored.FileDetailsStoreId).Files;
                Require(files.Count == 1 && files[0].Name == "second.bin" && files[0].TotalBytes == 5, "Previously recorded file missing from disk page");
                Require(folder.TotalBytes == 5 && stored.Root.TotalBytes == 12 && !stored.Complete &&
                    new[] { folder, stored.Root }.All(n => NodeSizeStatus.Get(n) == NodeSizeState.Partial && n.IncompleteReasons.HasFlag(ScanIncompleteReason.ReadError)), "Disk mode lost partial totals or failure reason");
            });
            Check("subthreshold files still qualify their directory across detail modes", () =>
            {
                var path = Path.Combine(scratch, "aggregate-threshold.jsonl");
                foreach (var mode in new[] { "memory", "summary", "disk" })
                {
                    ScanReport report;
                    using (var store = mode == "disk" ? new FileDetailStore(path) : null)
                        report = new DirectoryScanner().Scan(fixture, retainFileNodes: mode == "memory", fileDetails: store);
                    IEnumerable<ScanNode> candidates = report.Nodes;
                    if (mode == "disk") candidates = candidates.Concat(FileDetailStore.ReadAllFiles(path, report.FileDetailsStoreId));
                    var filter = NodeFilter.Create(new NodeFilterOptions { MinimumBytes = 12 });
                    var hits = ReportQuery.SelectTop(candidates, ReportScope.All, ReportOrder.Largest, 10, filter, default);
                    Require(report.Complete && report.Root.TotalBytes == 12 && hits.Count == 1 && hits[0] == report.Root,
                        mode + ": directory assembled from smaller files missing at exact threshold");
                }
            });
            Check("quick estimate excludes files at or below its threshold", () =>
            {
                var path = Path.Combine(scratch, "threshold.jsonl");
                ScanReport report;
                using (var store = new FileDetailStore(path))
                    report = new DirectoryScanner().Scan(fixture, retainFileNodes: false, fileDetails: store, minimumFileBytes: 6);
                var saved = FileDetailStore.ReadAllFiles(path, report.FileDetailsStoreId).Single();
                Require(report.Complete && report.Root.TotalFiles == 1 && report.Root.TotalBytes == 7 &&
                    saved.Name == "first.bin" && saved.TotalBytes == 7 && report.MinimumFileBytes == 6 && report.EstimateExcludedFiles == 2,
                    "Quick estimate included files at or below the threshold or saved the wrong files");
                var snapshot = Path.Combine(scratch, "threshold-snapshot.json");
                ReportWriter.Write(report, snapshot);
                var reopened = ReportReader.Read(snapshot);
                Require(reopened.MinimumFileBytes == 6 && reopened.EstimateExcludedFiles == 2 && reopened.Root.TotalFiles == 1 && reopened.Root.TotalBytes == 7 &&
                    FileDetailStore.ReadAllFiles(reopened.FileDetailsPath!, reopened.FileDetailsStoreId).Single().Name == saved.Name,
                    "Reopening a thresholded snapshot lost the threshold, totals, or retained detail");
            });
            Check("compact history preserves report mode, zero values and disk details", () =>
            {
                var details = Path.Combine(scratch, "compact-history.jsonl");
                ScanReport report;
                using (var store = new FileDetailStore(details))
                    report = new DirectoryScanner().Scan(fixture, retainFileNodes: false, fileDetails: store);
                var exported = Path.Combine(scratch, "history-export.json");
                var compact = Path.Combine(scratch, "history-compact.json");
                ReportWriter.Write(report, exported);
                ReportWriter.WriteHistorySnapshot(report, compact);
                var restored = ReportReader.Read(compact);
                Require(new FileInfo(compact).Length < new FileInfo(exported).Length,
                    "Compact history did not reduce serialized size");
                Require(restored.Complete && !restored.RetainsFileNodes && restored.Root.TotalBytes == 12
                    && restored.Root.TotalFiles == 3 && restored.FileDetailsStoreId == report.FileDetailsStoreId
                    && FileDetailStore.ReadPage(details, 0, expectedStoreId: restored.FileDetailsStoreId).Files.Any(n => n.TotalBytes == 0),
                    "Compact history changed report mode, totals or zero-byte detail");
                Console.WriteLine($"Compact fixture: {new FileInfo(exported).Length} -> {new FileInfo(compact).Length} bytes");
            });
            Check("allocated ranking uses deduplicated bytes and skips unknown allocations", () =>
            {
                var nodes = new[]
                {
                    new ScanNode { Name = "logical-large", Path = "logical-large", TotalBytes = 1000, TotalAllocatedBytes = 0 },
                    new ScanNode { Name = "unknown", Path = "unknown", TotalBytes = 1000 },
                    new ScanNode { Name = "allocated-large", Path = "allocated-large", TotalBytes = 20, TotalAllocatedBytes = 50 },
                    new ScanNode { Name = "duplicate", Path = "duplicate", TotalBytes = 20, FileAllocatedBytes = 50,
                        TotalAllocatedBytes = 0, HardLinkDuplicate = true }
                };
                var ranked = ReportQuery.SelectTop(nodes, ReportScope.All, ReportOrder.LargestAllocated, 10, null, default);
                Require(ranked.Count == 3 && ranked[0].Name == "allocated-large"
                    && ranked.Skip(1).All(node => node.TotalAllocatedBytes == 0),
                    "Allocation ranking used logical bytes or mixed unknown with zero");
            });
            Check("global ranking includes exact GiB across stored parents", () =>
            {
                const long gib = 1L << 30;
                var path = Path.Combine(scratch, "global-boundary.jsonl");
                Guid identity;
                using (var store = new FileDetailStore(path))
                {
                    identity = store.StoreId;
                    for (var i = 0; i < 3; i++)
                    {
                        var bytes = gib - 1 + i;
                        store.Append(new ScanNode { Id = i + 4, ParentId = i + 1, Name = "f" + i, Path = "D:\\p" + i + "\\f" + i,
                            OwnBytes = bytes, TotalBytes = bytes, TotalFiles = 1 });
                    }
                }
                var directory = new ScanNode { Id = 1, ParentId = 0, Name = "dir", Path = "D:\\dir", IsDirectory = true, TotalBytes = gib };
                var candidates = new[] { directory }.Concat(FileDetailStore.ReadAllFiles(path, identity));
                var filter = NodeFilter.Create(new NodeFilterOptions { MinimumBytes = gib });
                var rows = ReportQuery.SelectTop(candidates, ReportScope.All, ReportOrder.Largest, 10, filter, default);
                Require(rows.Count == 3 && rows[0].Name == "f2" && rows.Any(n => n.Name == "f1") && rows.Contains(directory), "Boundary or cross-parent match lost");
            });
            Check("drive roots and similar path prefixes do not bypass output exclusion", () =>
            {
                Require(!ScanCacheLocation.IsOutsideScan(@"D:\", @"D:\output.jsonl") && !ScanCacheLocation.IsOutsideScan(@"D:\", @"D:\")
                    && !ScanCacheLocation.IsOutsideScan(@"D:\data", @"D:\data\output.jsonl")
                    && ScanCacheLocation.IsOutsideScan(@"D:\data", @"D:\database\output.jsonl"), "Path containment mismatch");
            });
            Check("diagnostics classify native access errors and preserve legacy unknowns", () =>
            {
                var error = new IOException("native", new System.ComponentModel.Win32Exception(5));
                Require(ScanDiagnostics.ReasonFor(error) == ScanIncompleteReason.AccessDenied
                    && ScanDiagnostics.Issue(fixture, "metadata", error).ErrorCode == 5, "Native access error misclassified");
                var node = new ScanNode { Id = 0, Name = "old", Path = fixture, Incomplete = true };
                Require(ScanDiagnostics.DescribeNode(node).Contains("旧结果") && ScanDiagnostics.DescribeNode(node).Contains("未知"),
                    "Legacy partial result invented a cause or zero");
                var cancelledReport = new DirectoryScanner().Scan(fixture, new CancellationToken(true));
                var saved = Path.Combine(scratch, "cancelled.json");
                ReportWriter.Write(cancelledReport, saved);
                Require(ReportReader.Read(saved).Root.IncompleteReasons == ScanIncompleteReason.Cancelled,
                    "Incomplete reason lost on snapshot round trip");
            });
            Check("snapshot preflight chunk boundaries and envelope rejection", () =>
            {
                var json = JsonSerializer.Serialize(new DirectoryScanner().Scan(fixture));
                var path = Path.Combine(scratch, "chunked.json");
                File.WriteAllText(path, "{\"Padding\":\"" + new string('x', 70000) + "\"," + json[1..]);
                Require(ReportReader.Read(path).Root.TotalBytes == 12, "Cross-buffer token lost data");
                File.WriteAllText(path, json, new System.Text.UTF8Encoding(true));
                Require(ReportReader.Read(path).Root.TotalBytes == 12, "UTF-8 BOM snapshot rejected");
                var limited = false;
                try { ReportReader.Read(path, maximumNodes: 1); } catch (InvalidDataException) { limited = true; }
                Require(limited, "Node limit ignored");
                File.WriteAllText(path, "{\"Nodes\":[]," + json[1..]);
                var duplicate = false;
                try { ReportReader.Read(path); } catch (InvalidDataException) { duplicate = true; }
                Require(duplicate, "Duplicate envelope accepted");
                File.WriteAllText(path, json[..^1]);
                var truncated = false;
                try { ReportReader.Read(path); } catch (JsonException) { truncated = true; }
                Require(truncated, "Truncated snapshot accepted");
                File.WriteAllText(path, "{\"Padding\":\"" + new string('x', 1024 * 1024 + 1) + "\"," + json[1..]);
                var oversized = false;
                try { ReportReader.Read(path); }
                catch (InvalidDataException ex) { oversized = ex.Message.Contains("token exceeds"); }
                Require(oversized, "Oversized token bypassed bounded preflight buffer");
            });
            Check("malformed snapshot names and parent completeness are rejected", () =>
            {
                var report = new DirectoryScanner().Scan(fixture);
                var valid = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(report))!.AsObject();
                void Reject(System.Text.Json.Nodes.JsonObject value)
                {
                    var path = Path.Combine(scratch, "malformed.json");
                    File.WriteAllText(path, value.ToJsonString());
                    var rejected = false;
                    try { ReportReader.Read(path); } catch (InvalidDataException) { rejected = true; }
                    Require(rejected, "Malformed snapshot accepted");
                }
                var missingName = valid.DeepClone().AsObject();
                missingName["Nodes"]![0]!["Name"] = null;
                Reject(missingName);
                var missingRoot = valid.DeepClone().AsObject();
                missingRoot["RootPath"] = null;
                Reject(missingRoot);
                var falseComplete = valid.DeepClone().AsObject();
                var child = report.Nodes.Single(n => n.IsDirectory && n.ParentId == 0);
                falseComplete["Nodes"]![child.Id]!["Incomplete"] = true;
                Reject(falseComplete);
            });
            Check("corrupted and legacy indexes fall back without hiding files", () =>
            {
                var path = Path.Combine(scratch, "index-check.jsonl");
                using (var store = new FileDetailStore(path))
                {
                    store.Append(new ScanNode { Id = 1, ParentId = 0, Name = "a", Path = "D:\\a", OwnBytes = 7, TotalBytes = 7, TotalFiles = 1 });
                    store.Append(new ScanNode { Id = 2, ParentId = 1, Name = "b", Path = "D:\\b", OwnBytes = 5, TotalBytes = 5, TotalFiles = 1 });
                }
                var original = JsonSerializer.Deserialize<FileDetailStore.DetailIndex>(File.ReadAllText(path + ".index.json"))!;
                Require(original.Version == 1 && original.Digest?.Length == 64, "Missing index digest");
                void Verify(FileDetailStore.DetailIndex index)
                {
                    File.WriteAllText(path + ".index.json", JsonSerializer.Serialize(index));
                    Require(FileDetailStore.ReadPage(path, 0).Files.Single().Name == "a", "Directory 0 silently lost");
                    Require(FileDetailStore.ReadLargest(path, 1).Files.Single().Name == "b", "Directory 1 silently lost");
                }
                Verify(original);
                Verify(original with { Ranges = new() { [9] = original.Ranges[0], [1] = original.Ranges[1] } });
                Verify(original with { Ranges = new() { [0] = original.Ranges[1], [1] = original.Ranges[0] } });
                var first = original.Ranges[0][0];
                var second = original.Ranges[1][0];
                Verify(original with { Ranges = new() { [0] = [first, first], [1] = [second] } });
                Verify(original with { Ranges = new() { [0] = [first], [1] = [second with { Start = first.End - 1 }] } });
                Verify(original with { Ranges = new() { [0] = [first], [1] = [second with { Start = first.End + 1 }] } });
                Verify(original with { Version = 0, Digest = null });
            });
            Check("detail association rejects replacement and preserves legacy browsing", () =>
            {
                var path = Path.Combine(scratch, "association.jsonl");
                Guid identity;
                ScanReport report;
                using (var store = new FileDetailStore(path))
                {
                    identity = store.StoreId;
                    report = new DirectoryScanner().Scan(fixture, retainFileNodes: false, fileDetails: store);
                }
                var snapshot = Path.Combine(scratch, "association.json");
                ReportWriter.Write(report, snapshot);
                var loaded = ReportReader.Read(snapshot);
                Require(loaded.FileDetailsStoreId == identity, "Snapshot identity did not round-trip");
                Require(FileDetailStore.ReadPage(path, 0, expectedStoreId: identity).Files.Count == 2, "Matching identity failed");
                FileDetailStore.ValidateAssociation(path, identity);
                void Reject(Action read)
                {
                    var rejected = false;
                    try { read(); } catch (InvalidDataException) { rejected = true; }
                    Require(rejected, "Wrong scan identity accepted");
                }
                Reject(() => FileDetailStore.ReadPage(path, 0, expectedStoreId: Guid.NewGuid()));
                Reject(() => FileDetailStore.ReadLargest(path, 0, expectedStoreId: Guid.NewGuid()));
                var other = Path.Combine(scratch, "other-scan.jsonl");
                using (var empty = new FileDetailStore(other)) { }
                Reject(() => FileDetailStore.ReadPage(other, 0, expectedStoreId: identity));
                var legacy = Path.Combine(scratch, "legacy-detail.jsonl");
                File.WriteAllText(legacy, JsonSerializer.Serialize(new ScanNode { Id = 1, ParentId = 0, Path = "D:\\a", Name = "a", TotalFiles = 1 }) + "\n");
                Require(FileDetailStore.ReadPage(legacy, 0).Files.Count == 1, "Legacy record lost");
                Reject(() => FileDetailStore.ReadPage(legacy, 0, expectedStoreId: identity));
                var malformed = Path.Combine(scratch, "bad-header.jsonl");
                File.WriteAllText(malformed, "{\"DetailSchema\":42,\"StoreId\":\"" + identity + "\"}\n");
                Reject(() => FileDetailStore.ValidateAssociation(malformed, identity));
            });
            Console.WriteLine($"Audit checks: {count} passed, {failures.Count} failed; only tiny input files, no drive scan.");
            if (failures.Count > 0) throw new Exception("Audit failures: " + string.Join("; ", failures));
        }
        finally
        {
            var parent = Path.GetFullPath(Path.Combine(project, "research")) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(scratch);
            if (resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("audit-tiny-", StringComparison.Ordinal)
                && (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static string ResolveProjectRoot()
    {
        var configured = Environment.GetEnvironmentVariable("FLASHSCOPE_AUDIT_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
            if (!Directory.Exists(Path.Combine(full, "src", "FolderScope.Core")))
                throw new InvalidOperationException(
                    "FLASHSCOPE_AUDIT_ROOT 未指向 Flash-X 仓库根目录（缺少 src/FolderScope.Core）：" + full);
            return full;
        }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "FolderScope.Core", "FolderScope.Core.csproj")))
                return directory.FullName;
        throw new InvalidOperationException(
            "未找到 Flash-X 仓库根目录。请设置环境变量 FLASHSCOPE_AUDIT_ROOT=<仓库根目录> 后重试。");
    }
}
