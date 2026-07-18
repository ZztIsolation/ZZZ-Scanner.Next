using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace ZZZScannerHelper;

internal static class HelperSecurity
{
    public const int SupportedManifestSchemaVersion = 3;

    private static readonly string[] RuntimeOutputDirectories = ["Scans", "StabilitySuites"];

    public static void ValidateManifest(ScannerManifest manifest, Uri manifestUri, Version helperVersion)
    {
        EnsureTrustedDownloadUri(manifestUri, "manifest");

        if (manifest.SchemaVersion is not (1 or 2 or SupportedManifestSchemaVersion))
        {
            throw new InvalidDataException(
                $"Unsupported scanner manifest schema {manifest.SchemaVersion}; expected 1, 2 or {SupportedManifestSchemaVersion}.");
        }

        if (!Version.TryParse(manifest.LauncherMinVersion, out var minimumHelperVersion))
        {
            throw new InvalidDataException("Scanner manifest launcherMinVersion is invalid.");
        }

        if (helperVersion < minimumHelperVersion)
        {
            throw new InvalidOperationException(
                $"Scanner manifest requires helper {minimumHelperVersion} or newer; current helper is {helperVersion}.");
        }

        if (!IsSafeVersionSegment(manifest.ScannerVersion))
        {
            throw new InvalidDataException("Scanner manifest scannerVersion is invalid.");
        }

        if (manifest.SchemaVersion == 1)
        {
            ValidatePackage(manifest.LegacyPackage());
            return;
        }

        if (manifest.Support is null
            || !manifest.Support.Os.Equals("windows", StringComparison.OrdinalIgnoreCase)
            || manifest.Support.MinWindowsBuild < 17763
            || !manifest.Support.Architectures.Contains("x64", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Scanner manifest support must declare Windows build 17763+ and x64.");
        }

        if (manifest.Packages.Count < 2)
        {
            throw new InvalidDataException("Scanner manifest schema 2 must contain framework-dependent and self-contained packages.");
        }

        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in manifest.Packages)
        {
            ValidatePackage(package);
            if (manifest.SchemaVersion >= 3)
            {
                ValidatePackageFiles(package);
            }
            if (!packageIds.Add(package.Id))
            {
                throw new InvalidDataException($"Scanner manifest contains duplicate package id: {package.Id}.");
            }
        }

        var frameworkDependent = manifest.Packages.SingleOrDefault(package =>
            package.Mode.Equals(ScannerPackageModes.FrameworkDependent, StringComparison.OrdinalIgnoreCase));
        var selfContained = manifest.Packages.SingleOrDefault(package =>
            package.Mode.Equals(ScannerPackageModes.SelfContained, StringComparison.OrdinalIgnoreCase));
        if (frameworkDependent?.Framework is null
            || selfContained is null
            || selfContained.Framework is not null)
        {
            throw new InvalidDataException("Scanner manifest must contain one framework-dependent package with a framework and one self-contained package without a framework.");
        }
    }

    public static void ValidatePackage(ScannerPackage package)
    {
        if (!IsSafePackageId(package.Id))
        {
            throw new InvalidDataException($"Scanner package id is invalid: {package.Id}.");
        }

        if (!package.Mode.Equals(ScannerPackageModes.Legacy, StringComparison.OrdinalIgnoreCase)
            && !package.Mode.Equals(ScannerPackageModes.FrameworkDependent, StringComparison.OrdinalIgnoreCase)
            && !package.Mode.Equals(ScannerPackageModes.SelfContained, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Scanner package mode is invalid: {package.Mode}.");
        }

        if (!IsSafeEntryName(package.Entry))
        {
            throw new InvalidDataException("Scanner manifest entry must be a single executable filename.");
        }

        if (package.Size <= 0 || package.ExpandedSize < package.Size)
        {
            throw new InvalidDataException("Scanner package size and expandedSize must be positive and expandedSize must not be smaller than size.");
        }

        if (string.IsNullOrWhiteSpace(package.Sha256)
            || package.Sha256.Length != 64
            || package.Sha256.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException("Scanner package sha256 must be a 64-character hexadecimal digest.");
        }

        if (package.PackageUrls.Count == 0)
        {
            throw new InvalidDataException("Scanner package does not contain a package URL.");
        }

        if (package.Framework is not null
            && (string.IsNullOrWhiteSpace(package.Framework.Name)
                || package.Framework.Major <= 0
                || !Version.TryParse(package.Framework.MinVersion, out var minimum)
                || minimum.Major != package.Framework.Major))
        {
            throw new InvalidDataException("Scanner package framework requirement is invalid.");
        }
    }

    public static void EnsureTrustedDownloadUri(Uri uri, string purpose)
    {
        if (!uri.IsAbsoluteUri)
        {
            throw new InvalidOperationException($"Scanner {purpose} URL must be absolute.");
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && IsLoopbackHost(uri.Host))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Scanner {purpose} URL must use HTTPS; HTTP is allowed only for loopback development servers: {uri}");
    }

    public static string ResolvePathWithinRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Path must be relative to the scanner runtime root: {relativePath}");
        }

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relativePath));
        var rootPrefix = rootFull + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path escapes the scanner runtime root: {relativePath}");
        }

        return candidate;
    }

    public static async Task VerifyInstalledRuntimeAsync(
        string packagePath,
        string installDirectory,
        string entryName,
        CancellationToken token)
    {
        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var relativePath = NormalizeRelativePath(entry.FullName);
            if (!expectedFiles.Add(relativePath))
            {
                throw new InvalidDataException($"Scanner package contains duplicate path: {entry.FullName}");
            }

            var installedPath = ResolvePathWithinRoot(installDirectory, relativePath);
            if (!File.Exists(installedPath))
            {
                throw new InvalidDataException($"Scanner runtime file is missing: {relativePath}");
            }

            if (new FileInfo(installedPath).Length != entry.Length)
            {
                throw new InvalidDataException($"Scanner runtime file size mismatch: {relativePath}");
            }

            await using var packageStream = entry.Open();
            await using var installedStream = File.OpenRead(installedPath);
            var packageHash = await SHA256.HashDataAsync(packageStream, token);
            var installedHash = await SHA256.HashDataAsync(installedStream, token);
            if (!CryptographicOperations.FixedTimeEquals(packageHash, installedHash))
            {
                throw new InvalidDataException($"Scanner runtime file checksum mismatch: {relativePath}");
            }
        }

        var normalizedEntry = NormalizeRelativePath(entryName);
        if (!expectedFiles.Contains(normalizedEntry))
        {
            throw new InvalidDataException($"Scanner package does not contain the declared entry: {entryName}");
        }

        foreach (var installedPath in Directory.EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(installDirectory, installedPath));
            if (!expectedFiles.Contains(relativePath) && !IsRuntimeOutput(relativePath))
            {
                throw new InvalidDataException($"Scanner runtime contains an unexpected file: {relativePath}");
            }
        }
    }

    public static async Task VerifyInstalledRuntimeAsync(
        ScannerPackage package,
        string installDirectory,
        CancellationToken token)
    {
        ValidatePackageFiles(package);
        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in package.Files)
        {
            token.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(file.Path);
            if (!expectedFiles.Add(relativePath))
            {
                throw new InvalidDataException($"Scanner manifest contains duplicate file path: {file.Path}");
            }

            var installedPath = ResolvePathWithinRoot(installDirectory, relativePath);
            if (!File.Exists(installedPath))
            {
                throw new InvalidDataException($"Scanner runtime file is missing: {relativePath}");
            }

            if (new FileInfo(installedPath).Length != file.Size)
            {
                throw new InvalidDataException($"Scanner runtime file size mismatch: {relativePath}");
            }

            await using var installedStream = File.OpenRead(installedPath);
            var installedHash = await SHA256.HashDataAsync(installedStream, token);
            var expectedHash = Convert.FromHexString(file.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, installedHash))
            {
                throw new InvalidDataException($"Scanner runtime file checksum mismatch: {relativePath}");
            }
        }

        var normalizedEntry = NormalizeRelativePath(package.Entry);
        if (!expectedFiles.Contains(normalizedEntry))
        {
            throw new InvalidDataException($"Scanner package does not contain the declared entry: {package.Entry}");
        }

        foreach (var installedPath in Directory.EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(installDirectory, installedPath));
            if (!expectedFiles.Contains(relativePath) && !IsRuntimeOutput(relativePath))
            {
                throw new InvalidDataException($"Scanner runtime contains an unexpected file: {relativePath}");
            }
        }
    }

    private static void ValidatePackageFiles(ScannerPackage package)
    {
        if (package.Files.Count == 0)
        {
            throw new InvalidDataException("Scanner manifest v3 package must contain a file manifest.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;
        foreach (var file in package.Files)
        {
            var relativePath = NormalizeRelativePath(file.Path);
            if (string.IsNullOrWhiteSpace(relativePath)
                || Path.IsPathRooted(file.Path)
                || relativePath.Split('/').Any(segment => segment is "" or "." or "..")
                || !paths.Add(relativePath))
            {
                throw new InvalidDataException($"Scanner package file path is invalid or duplicated: {file.Path}");
            }
            if (file.Size < 0 || !IsSha256(file.Sha256))
            {
                throw new InvalidDataException($"Scanner package file metadata is invalid: {file.Path}");
            }
            totalSize = checked(totalSize + file.Size);
        }

        if (totalSize != package.ExpandedSize)
        {
            throw new InvalidDataException("Scanner package expandedSize does not match its file manifest.");
        }
    }

    private static bool IsSha256(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length == 64
            && value.All(Uri.IsHexDigit);
    }

    private static bool IsSafeVersionSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        var parts = value.Split('.');
        return parts.Length is 3 or 4
            && parts.All(part => part.Length > 0 && part.All(char.IsDigit))
            && Version.TryParse(value, out _);
    }

    private static bool IsSafePackageId(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 64
            && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
    }

    private static bool IsSafeEntryName(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !Path.IsPathRooted(value)
            && Path.GetFileName(value).Equals(value, StringComparison.Ordinal)
            && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && Path.GetExtension(value).Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopbackHost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }

    private static bool IsRuntimeOutput(string relativePath)
    {
        var firstSegment = relativePath.Split('/', 2)[0];
        return RuntimeOutputDirectories.Contains(firstSegment, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }
}

internal sealed class ScannerManifest
{
    public int SchemaVersion { get; set; }
    public string LauncherMinVersion { get; set; } = "";
    public string ScannerVersion { get; set; } = "";
    public string PackageUrl { get; set; } = "";
    public List<string> PackageUrls { get; set; } = [];
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Entry { get; set; } = "ZZZ-Scanner.Next.exe";
    public long ExpandedSize { get; set; }
    public ScannerSupport? Support { get; set; }
    public List<ScannerPackage> Packages { get; set; } = [];

    public IReadOnlyList<ScannerPackage> AvailablePackages()
    {
        return SchemaVersion == 1 ? [LegacyPackage()] : Packages;
    }

    public ScannerPackage LegacyPackage()
    {
        var urls = PackageUrls.ToList();
        if (!string.IsNullOrWhiteSpace(PackageUrl)
            && !urls.Contains(PackageUrl, StringComparer.OrdinalIgnoreCase))
        {
            urls.Add(PackageUrl);
        }

        return new ScannerPackage
        {
            Id = "win-x64-legacy",
            Mode = ScannerPackageModes.Legacy,
            PackageUrls = urls,
            Sha256 = Sha256,
            Size = Size,
            ExpandedSize = ExpandedSize > 0 ? ExpandedSize : Size,
            Entry = Entry
        };
    }
}

internal sealed class ScannerSupport
{
    public string Os { get; set; } = "windows";
    public List<string> Architectures { get; set; } = [];
    public int MinWindowsBuild { get; set; }
}

internal sealed class ScannerPackage
{
    public string Id { get; set; } = "";
    public string Mode { get; set; } = "";
    public ScannerFramework? Framework { get; set; }
    public List<string> PackageUrls { get; set; } = [];
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public long ExpandedSize { get; set; }
    public string Entry { get; set; } = "ZZZ-Scanner.Next.exe";
    public List<ScannerPackageFile> Files { get; set; } = [];
}

internal sealed class ScannerPackageFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

internal sealed class ScannerFramework
{
    public string Name { get; set; } = "";
    public int Major { get; set; }
    public string MinVersion { get; set; } = "";
}

internal static class ScannerPackageModes
{
    public const string Legacy = "legacy";
    public const string FrameworkDependent = "framework-dependent";
    public const string SelfContained = "self-contained";
}
