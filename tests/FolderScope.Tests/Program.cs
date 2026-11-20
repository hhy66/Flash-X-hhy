using System.Text.Json;
using System.Xml.Linq;
using FolderScope.Core;

if (args.Length == 1 && args[0] == "--audit-only") { AuditChecks.Run(); return; }
if (args.Length == 1 && args[0] == "--store-fault-only")
{
    var tinyRoot = Path.Combine(Path.GetTempPath(), "folderscope-store-fault-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tinyRoot);
    var detailPath = Path.Combine(tinyRoot, "one.jsonl");
    var store = new FileDetailStore(detailPath);
    store.Append(new ScanNode { Id = 1, ParentId = 0, Path = "C:\\sample.bin", Name = "sample.bin", OwnBytes = 7, TotalBytes = 7, TotalFiles = 1 });
    Directory.CreateDirectory(detailPath + ".index.json"); // Force only the optional index write to fail.
    store.Dispose(); store.Dispose();
    if (string.IsNullOrEmpty(store.IndexWriteError)) throw new Exception("Index failure was not recorded");
    var page = FileDetailStore.ReadPage(detailPath, 0);
    if (page.Files.Count != 1 || page.Files[0].TotalBytes != 7) throw new Exception("Authoritative detail record lost");
    var gapPath = Path.Combine(tinyRoot, "gap.jsonl");
    using (var gapStore = new FileDetailStore(gapPath)) gapStore.Append(page.Files[0]);
    var gapIndex = new FileDetailStore.DetailIndex(new FileInfo(gapPath).Length, File.GetLastWriteTimeUtc(gapPath).Ticks, new());
    File.WriteAllText(gapPath + ".index.json", JsonSerializer.Serialize(gapIndex));
    if (FileDetailStore.ReadPage(gapPath, 0).Files.Count != 1) throw new Exception("Missing index range silently omitted file");
    Console.WriteLine("PASS incomplete directory index falls back without hiding stored files.");
    var closedAppendRejected = false;
    try { store.Append(page.Files[0]); } catch (ObjectDisposedException) { closedAppendRejected = true; }
    if (!closedAppendRejected) throw new Exception("Closed store accepted append");
    Console.WriteLine("PASS closed store rejects append before mutating its index.");
    Console.WriteLine("PASS index save failure recorded; detail survives; repeated dispose safe.");
    var badRecordPath = Path.Combine(tinyRoot, "negative.jsonl");
    File.WriteAllText(badRecordPath, JsonSerializer.Serialize(new ScanNode { Id = 1, ParentId = 0, Path = "C:\\bad.bin", Name = "bad.bin", TotalBytes = -1 }) + "\n");
    var invalidRecordRejected = false;
    try { FileDetailStore.ReadPage(badRecordPath, 0); } catch (InvalidDataException) { invalidRecordRejected = true; }
    if (!invalidRecordRejected) throw new Exception("Negative stored file size accepted");
    Console.WriteLine("PASS invalid stored file metadata rejected.");
    var badSpacePath = Path.Combine(tinyRoot, "negative-space.jsonl");
    File.WriteAllText(badSpacePath, JsonSerializer.Serialize(new ScanNode { Id = 1, ParentId = 0, Path = "C:\\bad.bin", Name = "bad.bin", TotalFiles = 1, FileAllocatedBytes = -1 }) + "\n");
    foreach (var largest in new[] { false, true })
    {
        var allocatedRecordRejected = false;
        try { if (largest) FileDetailStore.ReadLargest(badSpacePath, 0); else FileDetailStore.ReadPage(badSpacePath, 0); }
        catch (InvalidDataException) { allocatedRecordRejected = true; }
        if (!allocatedRecordRejected) throw new Exception("Negative stored allocated size accepted");
    }
    Console.WriteLine("PASS negative allocated size rejected by paging and size ranking.");
    var partialPath = Path.Combine(tinyRoot, "partial-files.jsonl");
    using (var partialStore = new FileDetailStore(partialPath))
    {
        partialStore.Append(new ScanNode { Id = 1, ParentId = 0, Path = "C:\\unreadable.bin", Name = "unreadable.bin", Incomplete = true });
        partialStore.Append(new ScanNode { Id = 2, ParentId = 0, Path = "C:\\link.bin", Name = "link.bin", LinkSkipped = true });
    }
    var partialPage = FileDetailStore.ReadPage(partialPath, 0);
    if (partialPage.Files.Count != 2 || !partialPage.Files[0].Incomplete || !partialPage.Files[1].LinkSkipped
        || FileDetailStore.ReadLargest(partialPath, 0).Files.Count != 2)
        throw new Exception("Legitimate unknown/skipped file record was lost");
    Console.WriteLine("PASS unreadable and skipped file records preserve their unknown status in both readers.");
    var compactSpaceReport = new ScanReport { RootPath = "C:\\sample", RetainsFileNodes = false, MeasuresAllocatedSpace = true, AllocationTotalsVersion = 1 };
    compactSpaceReport.Nodes.Add(new ScanNode { Id = 0, Path = "C:\\sample", Name = "sample", IsDirectory = true,
        UnlistedFileBytes = 7, UnlistedFileCount = 1, TotalBytes = 12, TotalFiles = 2, TotalDirectories = 1,
        OwnAllocatedBytes = 0, UnlistedAllocatedBytes = 8, TotalAllocatedBytes = 16 });
    compactSpaceReport.Nodes.Add(new ScanNode { Id = 1, ParentId = 0, Path = "C:\\sample\\child", Name = "child", IsDirectory = true,
        UnlistedFileBytes = 5, UnlistedFileCount = 1, TotalBytes = 5, TotalFiles = 1,
        OwnAllocatedBytes = 0, UnlistedAllocatedBytes = 8, TotalAllocatedBytes = 8 });
    var compactSpacePath = Path.Combine(tinyRoot, "compact-space.json"); ReportWriter.Write(compactSpaceReport, compactSpacePath);
    if (ReportReader.Read(compactSpacePath).Root.TotalAllocatedBytes != 16) throw new Exception("Compact allocation round trip failed");
    compactSpaceReport.Root.TotalAllocatedBytes = 17;
    var corruptSpacePath = Path.Combine(tinyRoot, "compact-space-corrupt.json"); ReportWriter.Write(compactSpaceReport, corruptSpacePath);
    var corruptSpaceRejected = false;
    try { ReportReader.Read(corruptSpacePath); } catch (InvalidDataException) { corruptSpaceRejected = true; }
    if (!corruptSpaceRejected) throw new Exception("Corrupt compact allocation accepted");
    Console.WriteLine("PASS directory-summary allocated totals round trip and corrupt aggregate rejection.");
    var linkedDetailReport = new ScanReport { RootPath = "C:\\sample", RetainsFileNodes = false, FileDetailsPath = detailPath };
    linkedDetailReport.Nodes.Add(new ScanNode { Id = 0, Path = "C:\\sample", Name = "sample", IsDirectory = true });
    var linkedReportPath = Path.Combine(tinyRoot, "linked-report.json"); ReportWriter.Write(linkedDetailReport, linkedReportPath);
    if (ReportReader.Read(linkedReportPath).FileDetailsPath != detailPath) throw new Exception("Stored details reference lost");
    Console.WriteLine("PASS report retains external file-detail reference without rescanning.");
    var unknownSize = new ScanNode { Path = "unknown", Name = "unknown", Incomplete = true };
    var emptySize = new ScanNode { Path = "empty", Name = "empty" };
    var partialSize = new ScanNode { Path = "partial", Name = "partial", Incomplete = true, TotalBytes = 7 };
    if (NodeSizeStatus.SortableBytes(unknownSize) is not null || NodeSizeStatus.SortableBytes(emptySize) != 0
        || NodeSizeStatus.SortableBytes(partialSize) != 7 || NodeSizeStatus.SortableBytes(partialPage.Files[1]) is not null)
        throw new Exception("Unknown size sorted as known zero");
    Console.WriteLine("PASS unknown/skipped sorting is distinct from known zero and partial byte counts.");
    var rankingPath = Path.Combine(tinyRoot, "unknown-ranking.jsonl");
    using (var rankingStore = new FileDetailStore(rankingPath))
    {
        rankingStore.Append(new ScanNode { Id = 1, ParentId = 0, Path = "C:\\a-unknown", Name = "a-unknown", Incomplete = true });
        rankingStore.Append(new ScanNode { Id = 2, ParentId = 0, Path = "C:\\z-empty", Name = "z-empty", TotalFiles = 1 });
    }
    var ranked = FileDetailStore.ReadLargest(rankingPath, 0, 1);
    if (ranked.Files.Count != 1 || ranked.Files[0].Name != "z-empty" || !ranked.HasMore)
        throw new Exception("Unknown file displaced known empty file in size ranking");
    Console.WriteLine("PASS bounded size ranking keeps known zero ahead of unknown regardless of name.");
    foreach (var metric in new[] { "bytes", "files", "directories" })
    {
        var corruptLogical = ReportReader.Read(compactSpacePath);
        if (metric == "bytes") corruptLogical.Root.TotalBytes++;
        else if (metric == "files") corruptLogical.Root.TotalFiles++;
        else corruptLogical.Root.TotalDirectories++;
        var corruptLogicalPath = Path.Combine(tinyRoot, "corrupt-logical-" + metric + ".json");
        ReportWriter.Write(corruptLogical, corruptLogicalPath);
        var logicalRejected = false;
        try { ReportReader.Read(corruptLogicalPath); } catch (InvalidDataException) { logicalRejected = true; }
        if (!logicalRejected) throw new Exception("Corrupt logical aggregate accepted: " + metric);
    }
    Console.WriteLine("PASS reused aggregate buffer rejects corrupt bytes, file counts and directory counts.");
    if (ScanCacheLocation.IsOutsideScan("C:\\", "C:\\cache")
        || ScanCacheLocation.IsOutsideScan("D:\\data", "D:\\data\\cache")
        || ScanCacheLocation.IsOutsideScan("D:\\data", "D:\\data")
        || !ScanCacheLocation.IsOutsideScan("C:\\", "D:\\cache")
        || !ScanCacheLocation.IsOutsideScan("D:\\data", "D:\\database\\cache"))
        throw new Exception("Cache location overlap detection failed");
    Console.WriteLine("PASS automatic cache excludes scan root and descendants, allows other drives and sibling names.");
    Console.WriteLine("Tiny evidence: " + tinyRoot);
    return;
}
if (args.Length == 1 && args[0] == "--watchdog-only")
{
    var watchdogDeadline = new UnresponsiveDeadline(TimeSpan.FromMinutes(3));
    void ExpectWatchdog(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
    ExpectWatchdog(!watchdogDeadline.Observe(true, TimeSpan.FromHours(1)), "responsive long-running process never times out");
    ExpectWatchdog(!watchdogDeadline.Observe(false, TimeSpan.FromHours(1)), "first unresponsive observation starts timer");
    ExpectWatchdog(!watchdogDeadline.Observe(false, TimeSpan.FromHours(1) + TimeSpan.FromSeconds(179)), "179 seconds does not terminate");
    ExpectWatchdog(watchdogDeadline.Observe(false, TimeSpan.FromHours(1) + TimeSpan.FromSeconds(180)), "180 continuous seconds triggers timeout");
    ExpectWatchdog(!watchdogDeadline.Observe(true, TimeSpan.FromHours(2)), "recovery resets timer");
    ExpectWatchdog(!watchdogDeadline.Observe(false, TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1)), "new stall gets a fresh deadline");
    ExpectWatchdog(!watchdogDeadline.Observe(false, TimeSpan.FromHours(2) + TimeSpan.FromSeconds(180)), "old stall duration is not accumulated");
    Console.WriteLine("Watchdog timing checks: 7; no scan or test artifacts created.");
    return;
}
var baseDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "test-artifacts"));
var root = Path.Combine(baseDir, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var fixture = Path.Combine(root, "fixture");
Directory.CreateDirectory(Path.Combine(fixture, "child"));
Directory.CreateDirectory(Path.Combine(fixture, "空目录"));
File.WriteAllBytes(Path.Combine(fixture, "a.bin"), new byte[1024]);
File.WriteAllBytes(Path.Combine(fixture, "child", "b.bin"), new byte[2048]);
File.WriteAllText(Path.Combine(fixture, "child", "readme.txt"), "Folder Size CLI probe");
var scanner = new DirectoryScanner();
var directPage = DirectoryFileReader.Read(fixture);
if (directPage.Files.Count != 1 || directPage.Files[0].Name != "a.bin" || directPage.Truncated) throw new Exception("Direct file page must not recurse.");
var issueReport = new ScanReport { RootPath = fixture };
for (var i = 0; i < 10003; i++) issueReport.AddIssue(new ScanIssue(fixture, "probe", "synthetic error"));
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); checks++; Console.WriteLine("PASS " + name); }
// RFC4180-style split: paths may contain commas and escaped quotes, so line.Split(',') is unsafe.
static string[][] CsvCells(string[] lines)
{
    string[] Split(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted) { cells.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }
        cells.Add(current.ToString());
        return cells.ToArray();
    }
    return lines.Select(Split).ToArray();
}
var latestProgress = new LatestScanProgress();
Task.Run(() => { for (var i = 1; i <= 100000; i++) latestProgress.Report(new ScanProgress(i, "sample")); }).GetAwaiter().GetResult();
Check(latestProgress.TakeLatest()?.Entries == 100000 && latestProgress.TakeLatest() is null,
    "100000 worker progress updates retain only newest snapshot without backlog");
latestProgress.Report(new ScanProgress(100001, "next"));
Check(latestProgress.TakeLatest()?.Path == "next", "progress accepts new snapshot after consumption");
var report = scanner.Scan(fixture);
using var pageCancel = new CancellationTokenSource(); pageCancel.Cancel();
var pageCancelled = false;
try { DirectoryFileReader.Read(fixture, cancellationToken: pageCancel.Token); } catch (OperationCanceledException) { pageCancelled = true; }
Check(pageCancelled, "on-demand file reader observes cancellation");
var missingPageRejected = false;
try { DirectoryFileReader.Read(Path.Combine(root, "missing-page")); } catch (IOException) { missingPageRejected = true; }
Check(missingPageRejected, "missing on-demand directory does not appear empty");
foreach (var malformedEnvelope in new[] { "[]", "null", "42", "{\"Schema\":42}", "{\"Schema\":null}" })
{
    var envelopePath = Path.Combine(root, $"bad-envelope-{checks}.json");
    File.WriteAllText(envelopePath, malformedEnvelope);
    var envelopeRejected = false;
    try { ReportReader.Read(envelopePath); } catch (InvalidDataException) { envelopeRejected = true; }
    Check(envelopeRejected, $"invalid snapshot envelope rejected as data error: {malformedEnvelope}");
}
var overflowSnapshot = new ScanReport { RootPath = fixture, RetainsFileNodes = false };
overflowSnapshot.Nodes.Add(new ScanNode { Id = 0, Path = fixture, Name = "overflow", IsDirectory = true,
    OwnBytes = long.MaxValue, UnlistedFileBytes = 1, TotalBytes = long.MaxValue });
var overflowPath = Path.Combine(root, "overflow-initial-totals.json");
ReportWriter.Write(overflowSnapshot, overflowPath);
var overflowRejected = false;
try { ReportReader.Read(overflowPath); } catch (InvalidDataException) { overflowRejected = true; }
Check(overflowRejected, "initial snapshot byte overflow rejected as data error");
Check(report.Nodes.All(n => n.PathLength == n.Path.Length), "shared and stored path lengths match full paths");
using var stoppedSorting = new CancellationTokenSource(); stoppedSorting.Cancel();
var stoppedSortIndex = DirectoryIndex.Create(report, sortCancellationToken: stoppedSorting.Token);
Check(stoppedSortIndex.SortingCancelled && stoppedSortIndex.Children.Values.Sum(children => children.Count) == report.Nodes.Count - 1 && report.Complete,
    "stopping index sorting preserves every node and complete scan statistics");
if (OperatingSystem.IsWindows())
{
    var space = FileSpaceReader.Read(Path.Combine(fixture, "a.bin"));
    Check(space.LogicalBytes == 1024 && space.AllocatedBytes >= 0 && space.LinkCount >= 1,
        "Windows file allocation metadata preserves length and link count");
}
if (OperatingSystem.IsWindows())
{
    var allocatedFull = scanner.Scan(fixture, measureAllocatedSpace: true);
    var allocatedCompact = scanner.Scan(fixture, retainFileNodes: false, measureAllocatedSpace: true);
    var expectedAllocated = Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories).Sum(path => FileSpaceReader.Read(path).AllocatedBytes);
    Check(allocatedFull.Complete && allocatedCompact.Complete && allocatedFull.Root.TotalAllocatedBytes == expectedAllocated
        && allocatedCompact.Root.TotalAllocatedBytes == expectedAllocated && allocatedFull.Root.TotalBytes == 3093,
        "allocated totals agree across full and directory scans without changing logical bytes");
    var allocatedPath = Path.Combine(root, "allocated.json"); ReportWriter.Write(allocatedFull, allocatedPath);
    var allocatedRestored = ReportReader.Read(allocatedPath);
    Check(allocatedRestored.MeasuresAllocatedSpace && allocatedRestored.Root.TotalAllocatedBytes == expectedAllocated,
        "allocated measurement mode and totals survive JSON round trip");
    var allocatedLimited = scanner.Scan(fixture, maximumNodes: 2, measureAllocatedSpace: true);
    Check(!allocatedLimited.Complete && allocatedLimited.Root.TotalAllocatedBytes is null,
        "limited allocated scan reports unknown total rather than complete space");
}
var badAllocatedSnapshot = new ScanReport { RootPath = fixture, MeasuresAllocatedSpace = true };
badAllocatedSnapshot.Nodes.Add(new ScanNode { Id = 0, Path = fixture, Name = "bad-space", IsDirectory = true, TotalAllocatedBytes = -1 });
var badAllocatedPath = Path.Combine(root, "bad-allocated.json"); ReportWriter.Write(badAllocatedSnapshot, badAllocatedPath);
var badAllocatedRejected = false;
try { ReportReader.Read(badAllocatedPath); } catch (InvalidDataException) { badAllocatedRejected = true; }
Check(badAllocatedRejected, "negative allocated-space snapshot rejected");
Check(FileSpaceReader.MayRecallData(FileAttributes.Offline)
    && FileSpaceReader.MayRecallData((FileAttributes)0x40000)
    && FileSpaceReader.MayRecallData((FileAttributes)0x400000)
    && !FileSpaceReader.MayRecallData(FileAttributes.Archive | FileAttributes.Compressed | FileAttributes.SparseFile),
    "offline and recall flags conservatively block allocation handle query");
foreach (var retainDetailsOnFailure in new[] { true, false })
{
    var failingSpaceScanner = new DirectoryScanner(_ => throw new IOException("Injected allocation metadata failure"));
    var failedSpace = failingSpaceScanner.Scan(fixture, retainFileNodes: retainDetailsOnFailure, measureAllocatedSpace: true);
    Check(failedSpace.Root.TotalBytes == 3093 && failedSpace.Root.TotalFiles == 3 && failedSpace.Root.TotalAllocatedBytes is null
        && !failedSpace.Complete && failedSpace.Issues.Count(i => i.Operation == "allocated-space") == 3,
        $"allocation failures preserve known logical totals (retain={retainDetailsOnFailure})");
    var failureSnapshot = Path.Combine(root, $"failed-space-{retainDetailsOnFailure}.json");
    ReportWriter.Write(failedSpace, failureSnapshot);
    var restoredFailure = ReportReader.Read(failureSnapshot);
    Check(restoredFailure.Root.TotalBytes == 3093 && restoredFailure.Root.TotalFiles == 3
        && restoredFailure.Root.TotalAllocatedBytes is null && !restoredFailure.Complete,
        $"allocation failure snapshot preserves logical counts and unknown space (retain={retainDetailsOnFailure})");
}
foreach (var keepFilesWithoutIdentity in new[] { true, false })
{
    var missingIdentityScanner = new DirectoryScanner(path => new FileSpaceInfo(new FileInfo(path).Length, 4096, 2));
    var missingIdentityReport = missingIdentityScanner.Scan(fixture, retainFileNodes: keepFilesWithoutIdentity, measureAllocatedSpace: true);
    Check(missingIdentityReport.Root.TotalBytes == 3093 && missingIdentityReport.Root.TotalFiles == 3
        && missingIdentityReport.Root.TotalAllocatedBytes is null && missingIdentityReport.DuplicateHardLinkPaths == 0
        && missingIdentityReport.Issues.Count(i => i.Operation == "allocated-space") == 3 && !missingIdentityReport.Complete,
        $"unavailable hard-link identity never invents deduplication or zero space (retain={keepFilesWithoutIdentity})");
}
if (OperatingSystem.IsWindows())
{
    var inconsistentSpace = scanner.Scan(fixture, measureAllocatedSpace: true);
    inconsistentSpace.Root.TotalAllocatedBytes++;
    var inconsistentSpacePath = Path.Combine(root, "inconsistent-allocated-total.json");
    ReportWriter.Write(inconsistentSpace, inconsistentSpacePath);
    var inconsistentSpaceRejected = false;
    try { ReportReader.Read(inconsistentSpacePath); } catch (InvalidDataException) { inconsistentSpaceRejected = true; }
    Check(inconsistentSpaceRejected, "corrupt full-report allocated aggregate rejected");
}
var storedFilesPath = Path.Combine(root, "file-details.jsonl");
using (var store = new FileDetailStore(storedFilesPath))
{
    foreach (var file in report.Nodes.Where(n => !n.IsDirectory)) store.Append(file);
}
var storedChild = report.Nodes.Single(n => n.Name == "child");
var storedPage = FileDetailStore.ReadPage(storedFilesPath, storedChild.Id, limit: 1);
var storedNext = FileDetailStore.ReadPage(storedFilesPath, storedChild.Id, offset: 1, limit: 1);
Check(storedPage.Files.Count == 1 && storedPage.HasMore && storedNext.Files.Count == 1 && !storedNext.HasMore
    && storedPage.Files[0].Path != storedNext.Files[0].Path,
    "disk file-detail pages preserve directory membership and continuation");
Check(storedPage.Files.Concat(storedNext.Files).Sum(n => n.TotalBytes) == 2069,
    "disk file-detail pages preserve exact file sizes");
var scanDiskPath = Path.Combine(root, "scan-file-details.jsonl");
ScanReport diskScan;
using (var scanStore = new FileDetailStore(scanDiskPath))
    diskScan = scanner.Scan(fixture, retainFileNodes: false, measureAllocatedSpace: OperatingSystem.IsWindows(), fileDetails: scanStore);
var diskRootFiles = FileDetailStore.ReadPage(scanDiskPath, 0);
var diskChildId = diskScan.Nodes.Single(n => n.Name == "child").Id;
var diskChildFiles = FileDetailStore.ReadPage(scanDiskPath, diskChildId);
Check(diskScan.Complete && diskScan.Nodes.All(n => n.IsDirectory) && diskScan.Root.TotalBytes == 3093 && diskScan.Root.TotalFiles == 3
    && diskRootFiles.Files.Count == 1 && diskChildFiles.Files.Count == 2
    && diskRootFiles.Files.Concat(diskChildFiles.Files).Sum(n => n.TotalBytes) == 3093,
    "scan stores all file details on disk while retaining only directory nodes");
var diskLargest = FileDetailStore.ReadLargest(scanDiskPath, diskChildId, limit: 1);
Check(diskLargest.HasMore && diskLargest.Files.Count == 1 && diskLargest.Files[0].TotalBytes == 2048,
    "disk file-detail largest query is bounded and numerically ordered");
using var diskQueryCancel = new CancellationTokenSource(); diskQueryCancel.Cancel();
var diskQueryCancelled = false;
try { FileDetailStore.ReadLargest(scanDiskPath, diskChildId, cancellationToken: diskQueryCancel.Token); }
catch (OperationCanceledException) { diskQueryCancelled = true; }
Check(diskQueryCancelled, "disk largest query honors cancellation");
var storedIndexPath = scanDiskPath + ".index.json";
var validStoredIndex = File.ReadAllText(storedIndexPath);
File.WriteAllText(storedIndexPath, "{broken");
Check(FileDetailStore.ReadLargest(scanDiskPath, diskChildId, limit: 1).Files.Single().TotalBytes == 2048,
    "malformed disk-detail index falls back to authoritative records");
File.WriteAllText(storedIndexPath, validStoredIndex.Replace("\"Length\":", "\"Length\":0,\"IgnoredLength\":"));
Check(FileDetailStore.ReadPage(scanDiskPath, diskChildId).Files.Count == 2,
    "stale disk-detail length index falls back to sequential records");
File.WriteAllText(storedIndexPath, validStoredIndex);
var compact = scanner.Scan(fixture, retainFileNodes: false);
Check(compact.Nodes.All(n => n.IsDirectory) && compact.Root.TotalBytes == 3093 && compact.Root.TotalFiles == 3 && compact.Root.TotalDirectories == 2, "directory-only retention preserves full totals without file nodes");
var skippedOnly = new ScanReport { RootPath = fixture, UnlistedSkippedLinks = 1 };
Check(!skippedOnly.Complete, "unlisted skipped links prevent complete scan status");
var compactPath = Path.Combine(root, "compact.json"); ReportWriter.Write(compact, compactPath);
Check(ReportReader.Read(compactPath).Root.TotalBytes == 3093, "directory-only totals snapshot round trip");
var badCompactMode = Path.Combine(root, "bad-compact-mode.json");
File.WriteAllText(badCompactMode, File.ReadAllText(compactPath).Replace("\"RetainsFileNodes\": false", "\"RetainsFileNodes\": true"));
var badCompactRejected = false;
try { ReportReader.Read(badCompactMode); } catch (InvalidDataException) { badCompactRejected = true; }
Check(badCompactRejected, "unlisted file totals cannot masquerade as retained file details");
var badCompactCount = Path.Combine(root, "bad-compact-count.json");
File.WriteAllText(badCompactCount, File.ReadAllText(compactPath).Replace("\"UnlistedFileCount\": 1", "\"UnlistedFileCount\": -1"));
var negativeCompactRejected = false;
try { ReportReader.Read(badCompactCount); } catch (InvalidDataException) { negativeCompactRejected = true; }
Check(negativeCompactRejected, "negative directory-only file count rejected");
var cappedIssues = new ScanReport { RootPath = fixture };
for (var i = 0; i < 10000; i++) cappedIssues.AddIssue(new ScanIssue(fixture, "metadata", "probe"));
cappedIssues.AddIssue(new ScanIssue(fixture, "node-limit", "stopped"));
Check(cappedIssues.Issues.Count == 10000 && cappedIssues.SuppressedIssueCount == 1 && cappedIssues.Issues.Last().Operation == "node-limit", "safety stop reason survives full issue detail cap");
var sizeProbe = new ScanNode { Path = "probe", Name = "probe", IsDirectory = true };
Check(NodeSizeStatus.Get(sizeProbe) == NodeSizeState.Known, "empty complete directory is known zero");
sizeProbe.Incomplete = true;
Check(NodeSizeStatus.Get(sizeProbe) == NodeSizeState.Unknown, "unfinished zero directory has unknown size");
sizeProbe.TotalBytes = 10;
Check(NodeSizeStatus.Get(sizeProbe) == NodeSizeState.Partial, "unfinished nonzero directory is partial");
sizeProbe.LinkSkipped = true;
Check(NodeSizeStatus.Get(sizeProbe) == NodeSizeState.SkippedLink, "skipped link overrides numeric size state");
var directoryIndex = DirectoryIndex.Create(report);
var unsortedIndex = DirectoryIndex.Create(report, sortChildren: false);
Check(unsortedIndex.Children.Values.Sum(children => children.Count) == report.Nodes.Count - 1 && unsortedIndex.DirectDirectories.GetValueOrDefault(0) == 2 && unsortedIndex.Children[0].Select(n => n.Id).SequenceEqual(report.Nodes.Where(n => n.ParentId == 0).Select(n => n.Id)),
    "unsorted partial-result index retains nodes counts and discovery order");
Check(directoryIndex.Children.Values.Sum(n => n.Count) == report.Nodes.Count - 1 && directoryIndex.DirectDirectories.GetValueOrDefault(0) == 2,
    "single pass directory index retains every child and direct count");
Check(directoryIndex.Children[0].Select(n => n.TotalBytes).SequenceEqual(directoryIndex.Children[0].Select(n => n.TotalBytes).OrderDescending()),
    "directory index sorts children by descending bytes");
var synthetic = new ScanReport { RootPath = "synthetic" };
synthetic.Nodes.Add(new ScanNode { Id = 0, Path = "synthetic", Name = "synthetic", IsDirectory = true });
for (var i = 1; i <= 50000; i++) synthetic.Nodes.Add(new ScanNode { Id = i, ParentId = 0, Path = $"synthetic/{i:D6}", Name = i.ToString(), TotalBytes = (i * 7919L) % 100003 });
foreach (var node in synthetic.Nodes.Skip(1)) { node.OwnBytes = node.TotalBytes; node.TotalFiles = 1; }
synthetic.Root.TotalBytes = synthetic.Nodes.Skip(1).Sum(n => n.TotalBytes);
synthetic.Root.TotalFiles = 50000;
synthetic.FinishedUtc = DateTime.UtcNow;
var syntheticSnapshot = Path.Combine(root, "synthetic-50000.json");
ReportWriter.Write(synthetic, syntheticSnapshot);
var syntheticRestored = ReportReader.Read(syntheticSnapshot);
Check(syntheticRestored.Nodes.Count == 50001 && syntheticRestored.Root.TotalFiles == 50000 && syntheticRestored.Root.TotalBytes == synthetic.Root.TotalBytes, "50000-node UI fixture snapshot round trip");
var indexBefore = GC.GetAllocatedBytesForCurrentThread();
var indexWatch = System.Diagnostics.Stopwatch.StartNew();
var syntheticIndex = DirectoryIndex.Create(synthetic);
indexWatch.Stop();
var indexAllocated = GC.GetAllocatedBytesForCurrentThread() - indexBefore;
Check(syntheticIndex.Children[0].Count == 50000 && syntheticIndex.Children[0][0].TotalBytes == synthetic.Nodes.Skip(1).Max(n => n.TotalBytes), "50000-node directory index retains and orders children");
File.WriteAllText(Path.Combine(root, "directory-index-measurement.json"), JsonSerializer.Serialize(new { Nodes = 50000, Milliseconds = indexWatch.Elapsed.TotalMilliseconds, AllocatedBytes = indexAllocated, DiskScan = false }));
Console.WriteLine($"Synthetic index: {indexWatch.Elapsed.TotalMilliseconds:F2} ms, allocated {indexAllocated} bytes; no disk scan");
using (var measuredProcess = System.Diagnostics.Process.GetCurrentProcess())
{
    measuredProcess.Refresh();
    var memory = new { Nodes = 50000, ManagedBytes = GC.GetTotalMemory(false), measuredProcess.WorkingSet64, measuredProcess.PrivateMemorySize64, measuredProcess.PeakWorkingSet64, DiskScan = false, DesktopUI = false };
    File.WriteAllText(Path.Combine(root, "synthetic-process-memory.json"), JsonSerializer.Serialize(memory));
    Console.WriteLine($"Synthetic process memory: working={memory.WorkingSet64}, private={memory.PrivateMemorySize64}, peak working={memory.PeakWorkingSet64}; includes test runtime");
}
using var reportCancel = new CancellationTokenSource();
reportCancel.Cancel();
var indexCancelled = false;
try { DirectoryIndex.Create(synthetic, reportCancel.Token); } catch (OperationCanceledException) { indexCancelled = true; }
Check(indexCancelled, "directory index observes pre-cancellation");
var reportPreCancelled = false;
try { ReportQuery.SelectTop(synthetic, ReportScope.Files, ReportOrder.Largest, 1000, cancellationToken: reportCancel.Token); } catch (OperationCanceledException) { reportPreCancelled = true; }
Check(reportPreCancelled, "bounded report observes pre-cancellation");
using var reportMidCancel = new CancellationTokenSource();
var visited = 0;
var reportMidCancelled = false;
try { ReportQuery.SelectTop(synthetic, ReportScope.Files, ReportOrder.Largest, 1000, n => { if (++visited == 100) reportMidCancel.Cancel(); return true; }, reportMidCancel.Token); } catch (OperationCanceledException) { reportMidCancelled = true; }
Check(reportMidCancelled && visited == 100, "bounded report stops during traversal without partial return");
using var lastNodeCancel = new CancellationTokenSource();
var lastNodeCancelled = false;
try { ReportQuery.SelectTop(synthetic, ReportScope.Files, ReportOrder.Largest, 1000, n => { if (n.Id == 50000) lastNodeCancel.Cancel(); return true; }, lastNodeCancel.Token); } catch (OperationCanceledException) { lastNodeCancelled = true; }
Check(lastNodeCancelled, "bounded report observes cancellation on final candidate");
var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
var queryWatch = System.Diagnostics.Stopwatch.StartNew();
var syntheticTop = ReportQuery.SelectTop(synthetic, ReportScope.Files, ReportOrder.Largest, 1000);
queryWatch.Stop();
var queryAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
Check(syntheticTop.Select(n => n.Id).SequenceEqual(ReportQuery.Select(synthetic, ReportScope.Files, ReportOrder.Largest).Take(1000).Select(n => n.Id)), "50000-node bounded report matches full sort");
File.WriteAllText(Path.Combine(root, "bounded-report-measurement.json"), JsonSerializer.Serialize(new { Nodes = 50000, Limit = 1000, Milliseconds = queryWatch.Elapsed.TotalMilliseconds, AllocatedBytes = queryAllocated, DiskScan = false }));
Console.WriteLine($"Synthetic top query: {queryWatch.Elapsed.TotalMilliseconds:F2} ms, allocated {queryAllocated} bytes; no disk scan");
var chartNodes = Enumerable.Range(1, 12).Select(i => new ScanNode { Id = i, Path = i.ToString(), Name = i.ToString(), TotalBytes = i * 100 }).ToArray();
var summary = ChartSummary.Create(chartNodes);
using var chartCancelled = new CancellationTokenSource();
chartCancelled.Cancel();
var chartPreCancelled = false;
try { ChartSummary.Create(chartNodes, chartCancelled.Token); } catch (OperationCanceledException) { chartPreCancelled = true; }
Check(chartPreCancelled, "chart summary observes pre-cancellation");
using var chartMidCancel = new CancellationTokenSource();
IEnumerable<ScanNode> ChartStream()
{
    foreach (var node in chartNodes) { if (node.Id == 4) chartMidCancel.Cancel(); yield return node; }
}
var chartMidCancelled = false;
try { ChartSummary.Create(ChartStream(), chartMidCancel.Token); } catch (OperationCanceledException) { chartMidCancelled = true; }
Check(chartMidCancelled, "chart summary stops during enumeration");
Check(summary.Count == 9 && summary.Take(8).Select(n => n.TotalBytes).SequenceEqual(new long[] {1200,1100,1000,900,800,700,600,500}) && summary.Last().TotalBytes == 1000 && summary.Sum(n => n.TotalBytes) == 7800, "bounded chart summary preserves top eight and remaining total");
Check(ChartSummary.Create([]).Count == 0 && ChartSummary.Create(chartNodes.Take(8)).Count == 8, "chart summary handles empty and no remainder");
Check(issueReport.Issues.Count == 10000 && issueReport.SuppressedIssueCount == 3 && !issueReport.Complete, "issue detail cap preserves suppressed count and incomplete status");
Check(report.Root.TotalBytes == 3093 && report.Root.TotalFiles == 3 && report.Root.TotalDirectories == 2, "exact bytes/counts including empty directory");
Check(report.Nodes.Single(n => n.Name == "child").TotalBytes == 2069, "child aggregation");
Check(report.Complete && report.Issues.Count == 0, "complete regular fixture");
foreach (var order in Enum.GetValues<ReportOrder>())
    Check(ReportQuery.SelectTop(report, ReportScope.All, order, 2).Select(n => n.Id).SequenceEqual(ReportQuery.Select(report, ReportScope.All, order).Take(2).Select(n => n.Id)), "bounded report matches full ordering: " + order);
var memoryLimited = scanner.Scan(fixture, maximumManagedBytes: 1);
Check(!memoryLimited.Complete && memoryLimited.Root.Incomplete && memoryLimited.Issues.Any(i => i.Operation == "memory-limit"), "managed memory budget returns marked partial result");
var limited = scanner.Scan(fixture, maximumNodes: 2);
Check(limited.Nodes.Count == 2 && !limited.Complete && limited.Root.Incomplete && limited.Issues.Any(i => i.Operation == "node-limit"), "node safety limit returns marked partial result");
var limitedCsv = Path.Combine(root, "limited.csv");
ReportWriter.Write(limited, limitedCsv);
var limitedCsvCells = CsvCells(File.ReadAllLines(limitedCsv));
var limitedScanCompleteColumn = Array.IndexOf(limitedCsvCells[0], "ScanComplete");
Check(limitedScanCompleteColumn >= 0
    && limitedCsvCells.Skip(1).All(cells => cells[limitedScanCompleteColumn] == "False"),
    "node-limited CSV marks every row scan incomplete");
var limitedPath = Path.Combine(root, "limited.json");
ReportWriter.Write(limited, limitedPath);
var limitedRestored = ReportReader.Read(limitedPath);
Check(!limitedRestored.Complete && !limitedRestored.Cancelled && limitedRestored.Issues.Any(i => i.Operation == "node-limit") && limitedRestored.Root.TotalBytes == limited.Root.TotalBytes,
    "node-limited snapshot preserves reason and partial totals");
if (OperatingSystem.IsWindows())
{
    var allocated = AllocatedSpace.ReadFile(Path.Combine(fixture, "a.bin"));
    Check(allocated >= 1024, "Windows allocated bytes query for regular fixture");
    Console.WriteLine($"Allocated sample: logical=1024 allocated={allocated}");
    var sparseProbe = Environment.GetEnvironmentVariable("FOLDERSCOPE_SPARSE_PROBE");
    if (!string.IsNullOrWhiteSpace(sparseProbe))
    {
        var info = new FileInfo(sparseProbe);
        var sparseBytes = AllocatedSpace.ReadFile(sparseProbe);
        Check((info.Attributes & FileAttributes.SparseFile) != 0 && info.Length == 16777216 && sparseBytes < info.Length, "sparse probe API bytes below logical length");
        Console.WriteLine($"Sparse sample: logical={info.Length} API={sparseBytes}");
    }
}
var directCounts = ReportQuery.DirectDirectoryCounts(report);
Check(directCounts.GetValueOrDefault(0) == 2 && directCounts.GetValueOrDefault(report.Nodes.Single(n => n.Name == "child").Id) == 0, "direct directory counts differ from file counts");
Check(ReportQuery.Select(report, ReportScope.Files, ReportOrder.Largest).First().Name == "b.bin", "largest file excludes root and folders");
Check(ReportQuery.Select(report, ReportScope.Directories, ReportOrder.Largest).Select(n => n.Name).SequenceEqual(new[] { "child", "空目录" }), "largest directories includes empty directory");
var dated = report.Nodes.Single(n => n.Name == "readme.txt");
dated.ModifiedUtc = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
Check(ReportQuery.Select(report, ReportScope.All, ReportOrder.Oldest).First() == dated, "mixed oldest report uses modification time");
var longest = ReportQuery.Select(report, ReportScope.All, ReportOrder.LongestPath).ToList();
Check(longest.Count == report.Nodes.Count - 1 && longest.First().Path.Length == report.Nodes.Skip(1).Max(n => n.Path.Length), "long path report retains full paths and excludes root");
var binaries = report.Nodes.Where(NameFilter.Create("*.BIN;readme.?xt")).ToList();
Check(binaries.Count == 3 && binaries.Sum(n => n.TotalBytes) == 3093, "case insensitive multi-mask wildcard filter");
Check(report.Nodes.Where(NameFilter.Create("*.bin", exclude: true)).All(n => !n.Name.EndsWith(".bin")), "exclude wildcard filter");
var bounded = NodeFilter.Create(new NodeFilterOptions { Scope = ReportScope.Files, NameMasks = "*.bin", MinimumBytes = 1025, MaximumBytes = 2048 });
Check(report.Nodes.Where(bounded).Single().Name == "b.bin", "combined name size and file scope filter");
var oldFiles = NodeFilter.Create(new NodeFilterOptions { Scope = ReportScope.Files, ModifiedToUtc = new DateTime(2002, 1, 1) });
Check(report.Nodes.Where(oldFiles).Single().Name == "readme.txt", "modified date filter excludes newer files");
var directoriesOnly = NodeFilter.Create(new NodeFilterOptions { RequiredAttributes = (int)FileAttributes.Directory });
Check(report.Nodes.Where(directoriesOnly).All(n => n.IsDirectory), "required attribute filter");
var inverted = NodeFilter.Create(new NodeFilterOptions { Scope = ReportScope.Files, NameMasks = "*.bin", ExcludeMatches = true });
Check(report.Nodes.Where(inverted).Single().Name == "readme.txt", "exclude rule retains scope");
var exactSize = NodeFilter.Create(new NodeFilterOptions { Scope = ReportScope.Files, MinimumBytes = 1024, MaximumBytes = 1024 });
Check(report.Nodes.Where(exactSize).Single().Name == "a.bin", "size filter includes equal boundaries");
var unknownDate = new ScanNode { Id = 0, Path = "unknown", Name = "unknown" };
Check(!oldFiles(unknownDate), "date filter excludes missing timestamps");
var conflictRejected = false;
try { NodeFilter.Create(new NodeFilterOptions { RequiredAttributes = 16, ExcludedAttributes = 16 }); } catch (ArgumentException) { conflictRejected = true; }
Check(conflictRejected, "conflicting attribute rules rejected");
var reversedRejected = false;
try { NodeFilter.Create(new NodeFilterOptions { MinimumBytes = 100, MaximumBytes = 99 }); } catch (ArgumentException) { reversedRejected = true; }
Check(reversedRejected, "reversed byte bounds rejected");
Check(report.Nodes.All(n => n.CreatedUtc.HasValue && n.AccessedUtc.HasValue && n.ModifiedUtc.HasValue), "file and directory timestamps collected");
var json = Path.Combine(root, "report.json"); var csv = Path.Combine(root, "report.csv"); var xml = Path.Combine(root, "report.xml");
foreach (var path in new[] { json, csv, xml }) ReportWriter.Write(report, path);
var restored = ReportReader.Read(json);
var nullNodePath = Path.Combine(root, "null-node.json");
File.WriteAllText(nullNodePath, "{\"Schema\":\"folderscope.scan.v1\",\"RootPath\":\"probe\",\"Nodes\":[null],\"Issues\":[]}");
var nullNodeRejected = false;
try { ReportReader.Read(nullNodePath); } catch (InvalidDataException) { nullNodeRejected = true; }
Check(nullNodeRejected, "null snapshot node rejected without null-reference crash");
var badSuppressedPath = Path.Combine(root, "bad-suppressed.json");
File.WriteAllText(badSuppressedPath, File.ReadAllText(json).Replace("\"SuppressedIssueCount\": 0", "\"SuppressedIssueCount\": -1"));
var badSuppressedRejected = false;
try { ReportReader.Read(badSuppressedPath); } catch (InvalidDataException) { badSuppressedRejected = true; }
Check(badSuppressedRejected, "negative suppressed issue count rejected");
var issueSnapshot = new ScanReport { RootPath = report.RootPath, Nodes = report.Nodes, Issues = issueReport.Issues, SuppressedIssueCount = 3 };
var issueSnapshotPath = Path.Combine(root, "issues.json");
ReportWriter.Write(issueSnapshot, issueSnapshotPath);
Check(ReportReader.Read(issueSnapshotPath).SuppressedIssueCount == 3, "suppressed issue count survives snapshot round trip");
issueSnapshot.Issues.Add(new ScanIssue(fixture, "probe", "overflow detail"));
var excessiveIssuesPath = Path.Combine(root, "too-many-issues.json");
ReportWriter.Write(issueSnapshot, excessiveIssuesPath);
var excessiveIssuesRejected = false;
try { ReportReader.Read(excessiveIssuesPath); } catch (InvalidDataException ex) { excessiveIssuesRejected = ex.Message.Contains("detail limit"); }
Check(excessiveIssuesRejected, "excess snapshot issue details rejected");
var snapshotNodeLimit = false;
try { ReportReader.Read(json, maximumNodes: 2); } catch (InvalidDataException ex) { snapshotNodeLimit = ex.Message.Contains("node limit"); }
Check(snapshotNodeLimit, "snapshot node cap rejects before object deserialization");
Check(restored.Nodes.Count == report.Nodes.Count && restored.Root.TotalBytes == 3093 && restored.Nodes.Single(n => n.Name == "child").TotalBytes == 2069, "saved scan loads with exact hierarchy and sizes");
var malformed = Path.Combine(root, "bad-parent.json");
File.WriteAllText(malformed, File.ReadAllText(json).Replace("\"ParentId\": 0", "\"ParentId\": 999999"));
var invalidParentRejected = false;
try { ReportReader.Read(malformed); } catch (InvalidDataException) { invalidParentRejected = true; }
Check(invalidParentRejected, "corrupt snapshot parent rejected");
var oversized = Path.Combine(root, "oversized.json");
using (var oversizedStream = File.Create(oversized)) oversizedStream.SetLength(128L * 1024 * 1024 + 1);
var oversizedRejected = false;
// A 128 MiB zero-filled file would surface as JsonException if parsing ever started;
// catching InvalidDataException here proves the size gate rejected it before parsing.
try { ReportReader.Read(oversized); } catch (InvalidDataException) { oversizedRejected = true; }
Check(oversizedRejected, "oversized snapshot rejected before JSON parsing");
var badTotal = Path.Combine(root, "bad-total.json");
File.WriteAllText(badTotal, File.ReadAllText(json).Replace("\"TotalBytes\": 3093", "\"TotalBytes\": 3094"));
var invalidTotalRejected = false;
try { ReportReader.Read(badTotal); } catch (InvalidDataException) { invalidTotalRejected = true; }
Check(invalidTotalRejected, "corrupt snapshot aggregate rejected");
using var parsed = JsonDocument.Parse(File.ReadAllText(json));
Check(parsed.RootElement.GetProperty("Nodes").GetArrayLength() == report.Nodes.Count, "JSON node count");
var x = XDocument.Load(xml);
Check(x.Root!.Elements("node").Count() == report.Nodes.Count && File.ReadAllText(csv).Contains("空目录"), "XML nodes and UTF8 CSV");
var csvLines = File.ReadAllLines(csv);
var csvCells = CsvCells(csvLines);
var csvHeader = csvCells[0];
var csvNameColumn = Array.IndexOf(csvHeader, "Name");
var csvCreatedColumn = Array.IndexOf(csvHeader, "CreatedUtc");
var csvFileRow = csvCells.Skip(1).Single(cells => cells[csvNameColumn] == "a.bin");
Check(csvNameColumn >= 0 && csvCreatedColumn > 0
    && Array.IndexOf(csvHeader, "ScanComplete") >= 0
    && Array.IndexOf(csvHeader, "SizeState") >= 0
    && Array.IndexOf(csvHeader, "IsLowerBound") >= 0
    && csvFileRow[csvCreatedColumn] == report.Nodes.Single(n => n.Name == "a.bin").CreatedUtc!.Value.ToString("O"),
    "CSV appended metadata includes actual creation timestamp");
var fileXml = x.Root.Elements("node").Single(n => (string?)n.Attribute("name") == "a.bin");
Check((long?)fileXml.Attribute("ownBytes") == 1024 && fileXml.Attribute("attributes") != null && fileXml.Attribute("modifiedUtc") != null && x.Root.Attribute("startedUtc") != null, "XML includes file metadata and scan times");
var original = File.ReadAllBytes(json); var rejected = false;
try { ReportWriter.Write(report, json); } catch (IOException) { rejected = true; }
Check(rejected && original.SequenceEqual(File.ReadAllBytes(json)), "existing export preserved");
using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
Check(scanner.Scan(fixture, cancellation.Token).Cancelled, "cancellation reports partial state");
var interruptedFixture = Path.Combine(root, "interrupted");
Directory.CreateDirectory(Path.Combine(interruptedFixture, "pending-child"));
for (var i = 0; i < 250; i++) File.WriteAllBytes(Path.Combine(interruptedFixture, $"{i:D3}.bin"), new byte[2]);
var compactProgressValues = new List<int>();
var compactMany = scanner.Scan(interruptedFixture, progress: new ImmediateProgress(p => compactProgressValues.Add(p.Entries)), retainFileNodes: false);
Check(compactMany.Nodes.Count == 2 && compactMany.Root.TotalFiles == 250 && compactMany.Root.TotalBytes == 500 && compactProgressValues.Any(n => n >= 200) && compactProgressValues.Last() == 252,
    "directory-only progress counts visited files rather than retained nodes");
Check(compactProgressValues.Zip(compactProgressValues.Skip(1)).All(pair => pair.First <= pair.Second), "directory-only progress never moves backward");
var fullProgressValues = new List<int>();
var fullMany = scanner.Scan(interruptedFixture, progress: new ImmediateProgress(p => fullProgressValues.Add(p.Entries)));
Check(fullProgressValues.Zip(fullProgressValues.Skip(1)).All(pair => pair.First <= pair.Second), "full scan progress never moves backward");
Check(fullMany.Root.TotalBytes == compactMany.Root.TotalBytes && fullMany.Root.TotalFiles == compactMany.Root.TotalFiles && fullMany.Root.TotalDirectories == compactMany.Root.TotalDirectories && fullMany.Nodes.Count == 252 && compactMany.Nodes.Count == 2,
    "full and directory-only scans agree with 250 files while retaining 252 versus 2 nodes");
foreach (var sharedOrder in Enum.GetValues<ReportOrder>())
{
    var sharedQueryWatch = System.Diagnostics.Stopwatch.StartNew();
    var sharedQueryAllocatedStart = GC.GetAllocatedBytesForCurrentThread();
    var sharedTop = ReportQuery.SelectTop(fullMany, ReportScope.Files, sharedOrder, 25);
    var sharedQueryAllocated = GC.GetAllocatedBytesForCurrentThread() - sharedQueryAllocatedStart;
    sharedQueryWatch.Stop();
    Check(sharedTop.Select(n => n.Id).SequenceEqual(ReportQuery.Select(fullMany, ReportScope.Files, sharedOrder).Take(25).Select(n => n.Id)),
        "scanned shared-path report agrees with full ordering: " + sharedOrder);
    Console.WriteLine($"Shared-path 250-file query {sharedOrder}: {sharedQueryWatch.Elapsed.TotalMilliseconds:F2} ms, allocated {sharedQueryAllocated} bytes; not process peak");
}
var mixedPathsFixture = Path.Combine(root, "mixed-path-order");
foreach (var folderName in new[] { "a", "aa", "a-", "中文", "Z" })
{
    var folderPath = Path.Combine(mixedPathsFixture, folderName);
    Directory.CreateDirectory(folderPath);
    foreach (var fileName in new[] { "A.bin", "b.bin", "中文.bin", "long-name.bin" })
        File.WriteAllBytes(Path.Combine(folderPath, fileName), new byte[7]);
}
var mixedPathsReport = scanner.Scan(mixedPathsFixture);
Check(mixedPathsReport.Complete && mixedPathsReport.Root.TotalFiles == 20 && mixedPathsReport.Nodes.All(n => n.PathLength == n.Path.Length),
    "cross-directory shared paths preserve lengths and totals");
foreach (var mixedOrder in Enum.GetValues<ReportOrder>())
    Check(ReportQuery.SelectTop(mixedPathsReport, ReportScope.Files, mixedOrder, 13).Select(n => n.Path)
        .SequenceEqual(ReportQuery.Select(mixedPathsReport, ReportScope.Files, mixedOrder).Take(13).Select(n => n.Path)),
        "cross-directory shared-path ordering matches full paths: " + mixedOrder);
var actualLatestProgress = new LatestScanProgress();
var actualProgressReport = Task.Run(() => scanner.Scan(interruptedFixture, progress: actualLatestProgress)).GetAwaiter().GetResult();
var actualFinalProgress = actualLatestProgress.TakeLatest();
Check(actualProgressReport.Complete && actualProgressReport.Root.TotalFiles == 250
    && actualFinalProgress?.Entries == 252 && actualFinalProgress.Path == actualProgressReport.RootPath
    && actualLatestProgress.TakeLatest() is null,
    "real worker scan publishes exact final progress without queued stale paths");
var timedProgressEntries = new List<int>();
using var timedProgressCancel = new CancellationTokenSource();
var timedProgressReport = scanner.Scan(interruptedFixture, timedProgressCancel.Token, new ImmediateProgress(p =>
{
    timedProgressEntries.Add(p.Entries);
    if (p.Entries == 100) Thread.Sleep(100); // Simulate delayed progress delivery, not slow filesystem I/O.
    else if (p.Entries == 101) timedProgressCancel.Cancel();
}));
Check(timedProgressEntries.Contains(100) && timedProgressEntries.Contains(101),
    "elapsed-time progress reports before next hundred-item boundary");
Check(timedProgressReport.Cancelled && !timedProgressReport.Complete && timedProgressReport.Root.Incomplete,
    "elapsed-time progress callback cancellation preserves partial state");
var compactCsv = Path.Combine(root, "compact.csv");
ReportWriter.Write(compactMany, compactCsv);
var compactCells = CsvCells(File.ReadAllLines(compactCsv));
var compactHeader = compactCells[0];
var compactRootRow = compactCells.Skip(1).Single(cells => cells[Array.IndexOf(compactHeader, "Id")] == "0");
Check(compactRootRow[Array.IndexOf(compactHeader, "RetainsFileNodes")] == "False"
    && compactRootRow[Array.IndexOf(compactHeader, "UnlistedFileBytes")] == "500"
    && compactRootRow[Array.IndexOf(compactHeader, "UnlistedFileCount")] == "250",
    "directory-only CSV preserves mode and direct file totals");
var filesOnlyFixture = Path.Combine(root, "files-only");
Directory.CreateDirectory(filesOnlyFixture);
File.WriteAllBytes(Path.Combine(filesOnlyFixture, "one.bin"), new byte[11]);
File.WriteAllBytes(Path.Combine(filesOnlyFixture, "two.bin"), new byte[13]);
var rootOnly = scanner.Scan(filesOnlyFixture, maximumNodes: 1, retainFileNodes: false);
Check(rootOnly.Complete && rootOnly.Nodes.Count == 1 && rootOnly.Root.TotalBytes == 24 && rootOnly.Root.TotalFiles == 2,
    "directory-only node limit does not block unretained files");
var exactDirectoryLimit = scanner.Scan(interruptedFixture, maximumNodes: 2, retainFileNodes: false);
Check(exactDirectoryLimit.Complete && exactDirectoryLimit.Nodes.Count == 2 && exactDirectoryLimit.Root.TotalFiles == 250 && exactDirectoryLimit.Root.TotalBytes == 500,
    "directory retention at exact node cap continues counting all files");
var directoryOverflowFixture = Path.Combine(root, "directory-overflow");
Directory.CreateDirectory(Path.Combine(directoryOverflowFixture, "first"));
Directory.CreateDirectory(Path.Combine(directoryOverflowFixture, "second"));
var directoryOverflow = scanner.Scan(directoryOverflowFixture, maximumNodes: 2, retainFileNodes: false);
Check(!directoryOverflow.Complete && !directoryOverflow.Cancelled && directoryOverflow.Root.Incomplete && directoryOverflow.Nodes.Count == 2 && directoryOverflow.Issues.Any(i => i.Operation == "node-limit"),
    "directory retention exceeding cap reports partial rather than complete zero");
var directoryOverflowPath = Path.Combine(root, "directory-overflow.json");
ReportWriter.Write(directoryOverflow, directoryOverflowPath);
var restoredDirectoryOverflow = ReportReader.Read(directoryOverflowPath);
Check(!restoredDirectoryOverflow.Complete && !restoredDirectoryOverflow.RetainsFileNodes && restoredDirectoryOverflow.Root.Incomplete && restoredDirectoryOverflow.Issues.Any(i => i.Operation == "node-limit"),
    "directory cap snapshot preserves partial mode and stop reason");
var boundaryFixture = Path.Combine(root, "cancel-at-last-progress");
Directory.CreateDirectory(boundaryFixture);
for (var i = 0; i < 99; i++) File.WriteAllBytes(Path.Combine(boundaryFixture, $"{i:D3}.bin"), new byte[2]);
foreach (var retainDetails in new[] { true, false })
{
    using var boundaryCancel = new CancellationTokenSource();
    var boundaryReport = scanner.Scan(boundaryFixture, boundaryCancel.Token,
        new ImmediateProgress(p => { if (p.Entries == 100) boundaryCancel.Cancel(); }), retainFileNodes: retainDetails);
    Check(boundaryReport.Cancelled && !boundaryReport.Complete && boundaryReport.Root.Incomplete && boundaryReport.Root.TotalFiles == 98,
        $"last periodic progress cancellation preserves partial state (file details={retainDetails})");
}
using var compactCancel = new CancellationTokenSource();
var cancelledCompact = scanner.Scan(interruptedFixture, compactCancel.Token, new ImmediateProgress(p => { if (p.Entries >= 100) compactCancel.Cancel(); }), retainFileNodes: false);
Check(cancelledCompact.Cancelled && !cancelledCompact.Complete && cancelledCompact.Root.Incomplete && cancelledCompact.Root.TotalFiles < 250 && cancelledCompact.Nodes.All(n => n.IsDirectory), "directory-only cancellation retains explicitly partial totals");
var cancelledCompactPath = Path.Combine(root, "compact-cancelled.json");
ReportWriter.Write(cancelledCompact, cancelledCompactPath);
var loadedCancelledCompact = ReportReader.Read(cancelledCompactPath);
Check(!loadedCancelledCompact.RetainsFileNodes && loadedCancelledCompact.Cancelled && loadedCancelledCompact.Root.TotalBytes == cancelledCompact.Root.TotalBytes, "cancelled directory-only snapshot preserves mode and totals");
var fullAllocationStart = GC.GetAllocatedBytesForCurrentThread();
var allocationFull = scanner.Scan(interruptedFixture);
var fullScanAllocated = GC.GetAllocatedBytesForCurrentThread() - fullAllocationStart;
var compactAllocationStart = GC.GetAllocatedBytesForCurrentThread();
var allocationCompact = scanner.Scan(interruptedFixture, retainFileNodes: false);
var compactScanAllocated = GC.GetAllocatedBytesForCurrentThread() - compactAllocationStart;
Check(allocationFull.Root.TotalBytes == allocationCompact.Root.TotalBytes && allocationFull.Root.TotalFiles == allocationCompact.Root.TotalFiles, "allocation probe modes preserve totals");
File.WriteAllText(Path.Combine(root, "scan-allocation-comparison.json"), JsonSerializer.Serialize(new { Files = 250, FullAllocatedBytes = fullScanAllocated, CompactAllocatedBytes = compactScanAllocated, IncludesUI = false }));
Console.WriteLine($"250-file scan allocation: full={fullScanAllocated}, compact={compactScanAllocated}; not process peak");
var limitedFiles = DirectoryFileReader.Read(interruptedFixture, 10);
Check(limitedFiles.Files.Count == 10 && limitedFiles.Truncated, "on-demand directory file page is bounded and nonrecursive");
using var interruptedToken = new CancellationTokenSource();
var interrupted = scanner.Scan(interruptedFixture, interruptedToken.Token, new ImmediateProgress(_ => interruptedToken.Cancel()));
Check(interrupted.Cancelled && interrupted.Root.Incomplete && interrupted.Nodes.Where(n => n.IsDirectory).All(n => n.Incomplete), "cancelled enumeration marks discovered unfinished directories");
var partialJson = Path.Combine(root, "partial.json");
ReportWriter.Write(interrupted, partialJson);
var partialRestored = ReportReader.Read(partialJson);
Check(partialRestored.Cancelled && !partialRestored.Complete && partialRestored.Root.TotalBytes == interrupted.Root.TotalBytes && partialRestored.Nodes.Count == interrupted.Nodes.Count,
    "cancelled snapshot loads while retaining partial state");
try
{
    var link = Path.Combine(fixture, "loop"); Directory.CreateSymbolicLink(link, fixture);
    var links = scanner.Scan(fixture);
    Check(links.Nodes.Single(n => n.Name == "loop").LinkSkipped && links.Root.TotalBytes == 3093 && !links.Complete, "directory link skipped without recursion");
}
catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
{ Console.WriteLine("NOT TESTED link creation: " + ex.Message); }
var missing = false; try { scanner.Scan(Path.Combine(root, "missing")); } catch (DirectoryNotFoundException) { missing = true; }
Check(missing, "invalid root rejected");
Console.WriteLine($"Completed {checks} checks; artifacts retained at {root}");

sealed class ImmediateProgress(Action<ScanProgress> callback) : IProgress<ScanProgress>
{
    public void Report(ScanProgress value) => callback(value);
}
