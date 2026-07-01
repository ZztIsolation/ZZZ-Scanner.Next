using System.Diagnostics;
using System.Reflection;

namespace ZZZScannerNext.Core;

public static class AppInfo
{
    public const string Version = "1.0.30";

    public static string ExecutablePath => Environment.ProcessPath ?? AppContext.BaseDirectory;

    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string AssemblyVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    public static DateTimeOffset? ExecutableLastWriteTime
    {
        get
        {
            try
            {
                var path = ExecutablePath;
                return File.Exists(path) ? new FileInfo(path).LastWriteTime : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public static string FileVersion
    {
        get
        {
            try
            {
                var path = ExecutablePath;
                return File.Exists(path)
                    ? FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown"
                    : "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }

    public static object DiagnosticPayload() => new
    {
        appVersion = Version,
        assemblyVersion = AssemblyVersion,
        fileVersion = FileVersion,
        executablePath = ExecutablePath,
        executableLastWriteTime = ExecutableLastWriteTime?.ToString("O"),
        runtimeDirectory = BaseDirectory
    };
}
