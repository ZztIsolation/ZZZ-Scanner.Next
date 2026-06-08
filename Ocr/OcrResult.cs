namespace ZZZScannerNext.Ocr;

public readonly record struct OcrResult(float Score, string Text)
{
    public override string ToString()
    {
        return $"{Score * 100:F1}% {Text}";
    }
}
