using System.Drawing;

namespace ZZZScannerNext.Core;

public static class ColorMath
{
    public static Color FromArgbArray(IReadOnlyList<int> values)
    {
        if (values.Count != 4)
        {
            throw new InvalidDataException("Color arrays must be [A,R,G,B].");
        }

        return Color.FromArgb(values[0], values[1], values[2], values[3]);
    }

    public static bool IsCloseTo(this Color current, Color expected, int tolerance)
    {
        return Math.Abs(current.R - expected.R) <= tolerance
            && Math.Abs(current.G - expected.G) <= tolerance
            && Math.Abs(current.B - expected.B) <= tolerance;
    }
}
