namespace ZZZScannerHelper;

internal sealed class HelperStorageManager
{
    private const string DataRootEnvironmentVariable = "ZZZ_SCANNER_DATA_ROOT";
    private const string PendingCleanupFile = "cleanup-pending.txt";
    private const string ActiveRuntimeFile = "active-runtime.txt";

    public HelperStorageManager(string? dataRoot = null)
    {
        DataRoot = Path.GetFullPath(dataRoot ?? DefaultDataRoot());
        RuntimeRoot = Path.Combine(DataRoot, "runtime");
        PackageRoot = Path.Combine(DataRoot, "packages");
        OutputRoot = Path.Combine(DataRoot, "outputs");
        LogRoot = Path.Combine(DataRoot, "logs");
        HelperRoot = Path.Combine(DataRoot, "helper");
    }

    public string DataRoot { get; }
    public string RuntimeRoot { get; }
    public string PackageRoot { get; }
    public string OutputRoot { get; }
    public string LogRoot { get; }
    public string HelperRoot { get; }

    public static string DefaultDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZZZScannerNext")
            : configured;
    }

    public HelperStorageSnapshot Inspect(string? activeVersion, string? activePackageId)
    {
        Directory.CreateDirectory(DataRoot);
        var activeRuntime = ActiveRuntimePath(activeVersion, activePackageId);
        var outputSessions = OutputSessions().ToList();
        var retainedOutputs = RetainedOutputPaths(outputSessions);
        var runtimeBytes = DirectorySize(RuntimeRoot);
        var packageBytes = DirectorySize(PackageRoot);
        var outputBytes = DirectorySize(OutputRoot);
        var logBytes = DirectorySize(LogRoot);
        var helperBytes = DirectorySize(HelperRoot);
        var reclaimableRuntime = Directory.Exists(RuntimeRoot)
            ? Directory.EnumerateDirectories(RuntimeRoot)
                .Sum(path => IsSameOrParent(path, activeRuntime) ? InactiveChildrenSize(path, activeRuntime) : DirectorySize(path))
            : 0L;
        var reclaimableOutputs = outputSessions
            .Where(session => !retainedOutputs.Contains(session.Path))
            .Sum(session => DirectorySize(session.Path));
        var reclaimableRoots = ReclaimableRoots(activeRuntime, retainedOutputs);
        var reclaimableTemporary = TemporaryFiles()
            .Where(file => !reclaimableRoots.Any(root => IsSameOrParent(root, file)))
            .Sum(file => new FileInfo(file).Length);

        return new HelperStorageSnapshot
        {
            Root = DataRoot,
            ActiveVersion = activeVersion ?? "",
            ActivePackageId = activePackageId ?? "",
            HelperBytes = helperBytes,
            PackageBytes = packageBytes,
            RuntimeBytes = runtimeBytes,
            OutputBytes = outputBytes,
            LogBytes = logBytes,
            TotalBytes = DirectorySize(DataRoot),
            ReclaimableBytes = packageBytes + reclaimableRuntime + reclaimableOutputs + reclaimableTemporary,
            OutputSessionCount = outputSessions.Count,
        };
    }

    public HelperActiveRuntime? LoadActiveRuntime()
    {
        var path = Path.Combine(DataRoot, ActiveRuntimeFile);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var parts = File.ReadAllText(path).Split('\t');
            if (parts.Length != 4
                || !Version.TryParse(parts[0], out _)
                || parts[1].Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            {
                return null;
            }
            var runtime = HelperSecurity.ResolvePathWithinRoot(RuntimeRoot, Path.Combine(parts[0], parts[1]));
            var entry = HelperSecurity.ResolvePathWithinRoot(runtime, parts[3]);
            return File.Exists(entry)
                ? new HelperActiveRuntime(parts[0], parts[1], parts[2], parts[3], entry)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public HelperActiveRuntime SaveActiveRuntime(string version, string packageId, string packageMode, string entryName)
    {
        var runtime = HelperSecurity.ResolvePathWithinRoot(RuntimeRoot, Path.Combine(version, packageId));
        var entry = HelperSecurity.ResolvePathWithinRoot(runtime, entryName);
        if (!File.Exists(entry))
        {
            throw new FileNotFoundException("Cannot activate a missing scanner runtime entry.", entry);
        }
        Directory.CreateDirectory(DataRoot);
        var state = new HelperActiveRuntime(version, packageId, packageMode, entryName, entry);
        var temp = Path.Combine(DataRoot, ActiveRuntimeFile + ".tmp");
        File.WriteAllText(temp, string.Join('\t', version, packageId, packageMode, entryName));
        File.Move(temp, Path.Combine(DataRoot, ActiveRuntimeFile), overwrite: true);
        return state;
    }

    public HelperStorageCleanupResult Cleanup(string? activeVersion, string? activePackageId)
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(OutputRoot);
        var before = Inspect(activeVersion, activePackageId);
        var errors = new List<string>();

        RetryPendingCleanup(errors);
        MigrateRetainedLegacyOutputs(errors);
        PruneOutputs(errors);
        PrunePackages(errors);
        PruneRuntimes(activeVersion, activePackageId, errors);
        PruneTemporaryFiles(errors);

        var after = Inspect(activeVersion, activePackageId);
        SavePendingCleanup(errors);
        return new HelperStorageCleanupResult
        {
            Before = before,
            After = after,
            ReclaimedBytes = Math.Max(0, before.TotalBytes - after.TotalBytes),
            Errors = errors,
        };
    }

    private void MigrateRetainedLegacyOutputs(List<string> errors)
    {
        var candidates = OutputSessions().Concat(LegacyOutputSessions()).ToList();
        foreach (var candidate in RetainedOutputSessions(candidates).Where(candidate => candidate.Legacy))
        {
            var relative = Path.GetRelativePath(RuntimeRoot, candidate.Path)
                .Replace(Path.DirectorySeparatorChar, '-')
                .Replace(Path.AltDirectorySeparatorChar, '-');
            var destination = Path.Combine(OutputRoot, $"legacy-{relative}");
            if (Directory.Exists(destination))
            {
                continue;
            }

            try
            {
                Directory.Move(candidate.Path, destination);
            }
            catch (Exception ex)
            {
                errors.Add($"{candidate.Path}|{ex.Message}");
            }
        }
    }

    private void PruneOutputs(List<string> errors)
    {
        var sessions = OutputSessions().ToList();
        var retained = RetainedOutputPaths(sessions);
        foreach (var session in sessions.Where(session => !retained.Contains(session.Path)))
        {
            DeleteDirectory(session.Path, errors);
        }
    }

    private void PrunePackages(List<string> errors)
    {
        if (!Directory.Exists(PackageRoot))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(PackageRoot, "*", SearchOption.TopDirectoryOnly))
        {
            DeleteFile(file, errors);
        }
        foreach (var directory in Directory.EnumerateDirectories(PackageRoot))
        {
            DeleteDirectory(directory, errors);
        }
    }

    private void PruneRuntimes(string? activeVersion, string? activePackageId, List<string> errors)
    {
        if (!Directory.Exists(RuntimeRoot))
        {
            return;
        }

        var activeRuntime = ActiveRuntimePath(activeVersion, activePackageId);
        foreach (var versionDirectory in Directory.EnumerateDirectories(RuntimeRoot))
        {
            if (!IsSameOrParent(versionDirectory, activeRuntime))
            {
                DeleteDirectory(versionDirectory, errors);
                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(versionDirectory))
            {
                if (!PathEquals(child, activeRuntime))
                {
                    DeleteDirectory(child, errors);
                }
            }
            foreach (var file in Directory.EnumerateFiles(versionDirectory))
            {
                DeleteFile(file, errors);
            }
        }
    }

    private void PruneTemporaryFiles(List<string> errors)
    {
        if (!Directory.Exists(DataRoot))
        {
            return;
        }

        foreach (var file in TemporaryFiles().ToList())
        {
            DeleteFile(file, errors);
        }
        foreach (var directory in TemporaryDirectories().OrderByDescending(path => path.Length).ToList())
        {
            DeleteDirectory(directory, errors);
        }
    }

    private List<string> ReclaimableRoots(string? activeRuntime, HashSet<string> retainedOutputs)
    {
        var roots = new List<string> { PackageRoot };
        if (Directory.Exists(RuntimeRoot))
        {
            foreach (var versionDirectory in Directory.EnumerateDirectories(RuntimeRoot))
            {
                if (!IsSameOrParent(versionDirectory, activeRuntime))
                {
                    roots.Add(versionDirectory);
                    continue;
                }
                roots.AddRange(Directory.EnumerateDirectories(versionDirectory)
                    .Where(path => !PathEquals(path, activeRuntime)));
            }
        }
        roots.AddRange(OutputSessions()
            .Where(session => !retainedOutputs.Contains(session.Path))
            .Select(session => session.Path));
        return roots;
    }

    private IEnumerable<string> TemporaryFiles()
    {
        if (!Directory.Exists(DataRoot))
        {
            return [];
        }
        var temporaryDirectories = TemporaryDirectories().ToList();
        return Directory.EnumerateFiles(DataRoot, "*", SearchOption.AllDirectories)
            .Where(path => HasTemporarySuffix(path)
                || temporaryDirectories.Any(directory => IsSameOrParent(directory, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<string> TemporaryDirectories()
    {
        return Directory.Exists(DataRoot)
            ? Directory.EnumerateDirectories(DataRoot, "*", SearchOption.AllDirectories)
                .Where(HasTemporarySuffix)
            : [];
    }

    private static bool HasTemporarySuffix(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".download", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<OutputSession> OutputSessions()
    {
        if (!Directory.Exists(OutputRoot))
        {
            yield break;
        }
        foreach (var directory in Directory.EnumerateDirectories(OutputRoot))
        {
            yield return Session(directory, legacy: false);
        }
    }

    private IEnumerable<OutputSession> LegacyOutputSessions()
    {
        if (!Directory.Exists(RuntimeRoot))
        {
            yield break;
        }
        foreach (var scansRoot in Directory.EnumerateDirectories(RuntimeRoot, "Scans", SearchOption.AllDirectories))
        {
            foreach (var directory in Directory.EnumerateDirectories(scansRoot))
            {
                yield return Session(directory, legacy: true);
            }
        }
    }

    private static OutputSession Session(string path, bool legacy)
    {
        return new OutputSession(
            Path.GetFullPath(path),
            File.Exists(Path.Combine(path, "export.json")),
            Directory.GetLastWriteTimeUtc(path),
            legacy);
    }

    private static IEnumerable<OutputSession> RetainedOutputSessions(IEnumerable<OutputSession> sessions)
    {
        var list = sessions.ToList();
        var success = list.Where(session => session.Success).MaxBy(session => session.ModifiedUtc);
        var failed = list.Where(session => !session.Success).MaxBy(session => session.ModifiedUtc);
        if (success is not null)
        {
            yield return success;
        }
        if (failed is not null)
        {
            yield return failed;
        }
    }

    private static HashSet<string> RetainedOutputPaths(IEnumerable<OutputSession> sessions)
    {
        return RetainedOutputSessions(sessions)
            .Select(session => session.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private string? ActiveRuntimePath(string? activeVersion, string? activePackageId)
    {
        if (string.IsNullOrWhiteSpace(activeVersion) || string.IsNullOrWhiteSpace(activePackageId))
        {
            return null;
        }
        return Path.GetFullPath(Path.Combine(RuntimeRoot, activeVersion, activePackageId));
    }

    private static bool IsSameOrParent(string path, string? candidate)
    {
        if (candidate is null)
        {
            return false;
        }
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(full, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string path, string? candidate)
    {
        return candidate is not null
            && Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(candidate.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static long InactiveChildrenSize(string versionDirectory, string? activeRuntime)
    {
        return Directory.EnumerateDirectories(versionDirectory)
            .Where(path => !PathEquals(path, activeRuntime))
            .Sum(DirectorySize)
            + Directory.EnumerateFiles(versionDirectory).Sum(file => new FileInfo(file).Length);
    }

    private void RetryPendingCleanup(List<string> errors)
    {
        var path = Path.Combine(DataRoot, PendingCleanupFile);
        if (!File.Exists(path))
        {
            return;
        }
        foreach (var relative in File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            try
            {
                var target = HelperSecurity.ResolvePathWithinRoot(DataRoot, relative);
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{relative}|{ex.Message}");
            }
        }
        try { File.Delete(path); } catch { }
    }

    private void SavePendingCleanup(IEnumerable<string> errors)
    {
        var pending = errors.Select(error => error.Split('|', 2)[0])
            .Select(path => Path.IsPathRooted(path) ? Path.GetRelativePath(DataRoot, path) : path)
            .Where(path => !path.StartsWith("..", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pendingPath = Path.Combine(DataRoot, PendingCleanupFile);
        if (pending.Length == 0)
        {
            try { File.Delete(pendingPath); } catch { }
            return;
        }
        File.WriteAllLines(pendingPath, pending);
    }

    private static void DeleteDirectory(string path, List<string> errors)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) { errors.Add($"{path}|{ex.Message}"); }
    }

    private static void DeleteFile(string path, List<string> errors)
    {
        try { File.Delete(path); }
        catch (Exception ex) { errors.Add($"{path}|{ex.Message}"); }
    }

    private static long DirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch
        {
            return 0;
        }
    }

    private sealed record OutputSession(string Path, bool Success, DateTime ModifiedUtc, bool Legacy);
}

internal sealed record HelperActiveRuntime(
    string Version,
    string PackageId,
    string PackageMode,
    string EntryName,
    string EntryPath);

internal sealed class HelperStorageSnapshot
{
    public string Root { get; set; } = "";
    public string ActiveVersion { get; set; } = "";
    public string ActivePackageId { get; set; } = "";
    public long TotalBytes { get; set; }
    public long HelperBytes { get; set; }
    public long PackageBytes { get; set; }
    public long RuntimeBytes { get; set; }
    public long OutputBytes { get; set; }
    public long LogBytes { get; set; }
    public long ReclaimableBytes { get; set; }
    public int OutputSessionCount { get; set; }
}

internal sealed class HelperStorageCleanupResult
{
    public HelperStorageSnapshot Before { get; set; } = new();
    public HelperStorageSnapshot After { get; set; } = new();
    public long ReclaimedBytes { get; set; }
    public List<string> Errors { get; set; } = [];
}
