namespace ZZZScannerNext.Scanning;

public interface IScannerFailureException : IScanDiagnosticException
{
    string Code { get; }
    string Title { get; }
    string Remedy { get; }
    bool Retryable { get; }
}

public sealed class ScannerFailureException : InvalidOperationException, IScannerFailureException
{
    public ScannerFailureException(
        string code,
        string title,
        string message,
        string remedy,
        IReadOnlyDictionary<string, object?>? details = null,
        Exception? innerException = null,
        bool retryable = true)
        : base(message, innerException)
    {
        Code = code;
        Title = title;
        Remedy = remedy;
        Retryable = retryable;
        DiagnosticDetails = details ?? new Dictionary<string, object?>();
    }

    public string Code { get; }
    public string Title { get; }
    public string Remedy { get; }
    public bool Retryable { get; }
    public IReadOnlyDictionary<string, object?> DiagnosticDetails { get; }
}
