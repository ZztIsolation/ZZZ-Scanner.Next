using System.Drawing.Imaging;
using ZZZScannerNext.Ocr;

namespace ZZZScannerNext.Scanning.Gamepad;

internal static class DetailFrameSignature
{
    public static byte[] Signature(Bitmap frame, Rectangle[] regions)
    {
        // Each field is compared separately so a changing value cannot be diluted
        // by the large static black background of the entire details panel.

        using var small = new Bitmap(48, 12 * regions.Length, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(small))
            for (var i = 0; i < regions.Length; i++)
                graphics.DrawImage(frame, new Rectangle(0, i * 12, 48, 12), regions[i], GraphicsUnit.Pixel);
        var pixels = PaddleOcrPreprocessor.Read(small);
        var signature = new byte[small.Width * small.Height];
        for (var i = 0; i < signature.Length; i++)
            signature[i] = (byte)((pixels.Data[i * 3] + 2 * pixels.Data[i * 3 + 1] + pixels.Data[i * 3 + 2]) / 4);
        return signature;
    }

    public static double SignatureDifference(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0 || a.Length % 12 != 0) return 255;
        var length = a.Length / 12;
        var maximum = 0d;
        for (var field = 0; field < 12; field++)
        {
            var sum = 0d;
            for (var i = 0; i < length; i++) sum += Math.Abs(a[field * length + i] - b[field * length + i]);
            maximum = Math.Max(maximum, sum / length);
        }
        return maximum;
    }

    public static string DetectRarity(Bitmap frame, GridObservation grid)
    {
        var cell = grid.Cell(grid.SelectedRow, grid.SelectedColumn);
        var votes = new int[3];
        for (var iy = 0; iy < 5; iy++)
            for (var ix = 0; ix < 9; ix++)
            {
                var x = Math.Clamp((int)(cell.Left + cell.Width * (.2 + ix * .075)), 0, frame.Width - 1);
                var y = Math.Clamp((int)(cell.Top + cell.Height * (.91 + iy * .012)), 0, frame.Height - 1);
                var c = frame.GetPixel(x, y);
                if (c.R > 150 && c.G > 80 && c.B < 85 && c.R > c.G * 1.12) votes[0]++;
                if (c.R > 130 && c.B > 130 && c.G < 100) votes[1]++;
                if (c.B > 130 && c.G > 90 && c.R < 100) votes[2]++;
            }
        var best = Array.IndexOf(votes, votes.Max());
        if (votes[best] < 5) throw new InvalidOperationException("无法从当前盘品质条确认 S/A/B，停止 OCR 提交。");
        return new[] { "S", "A", "B" }[best];
    }

}
