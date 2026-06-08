namespace ZZZScannerNext.Core;

public static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string DataDirectory => Path.Combine(BaseDirectory, "Data");

    public static string ModelFile => Path.Combine(BaseDirectory, "Resources", "models", "PP-OCRv5_mobile_rec_infer.onnx");

    public static string CharacterDictFile => Path.Combine(BaseDirectory, "Resources", "models", "characterDict.txt");

    public static string ZzzFastModelFile => Path.Combine(BaseDirectory, "Resources", "models", "ZZZ_fast_rec.onnx");

    public static string ZzzFastCharacterDictFile => Path.Combine(BaseDirectory, "Resources", "models", "ZZZ_fast_dict.txt");

    public static string CreateScanDirectory()
    {
        var dir = Path.Combine(BaseDirectory, "Scans", DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string DataFile(string filename)
    {
        var outputPath = Path.Combine(DataDirectory, filename);
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var sourcePath = Path.Combine(FindProjectDirectory(), "Data", filename);
        if (File.Exists(sourcePath))
        {
            return sourcePath;
        }

        return outputPath;
    }

    private static string FindProjectDirectory()
    {
        var current = new DirectoryInfo(BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "ZZZ-Scanner.Next.csproj");
            if (File.Exists(candidate))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return BaseDirectory;
    }
}
