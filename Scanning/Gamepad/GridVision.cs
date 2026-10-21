using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ZZZScannerNext.Scanning.Gamepad;

internal sealed record GridRow(double Y, int[] Columns, byte[] Signature, bool FullyVisible);
internal sealed record GridObservation(int Width, int Height, Rectangle SearchArea, double[] ColumnX,
    GridRow[] Rows, int SelectedRow, int SelectedColumn, Rectangle Selection, double CellWidth, double CellHeight)
{
    public int Columns => ColumnX.Length;
    public int RowCount => Rows[SelectedRow].Columns.Length;
    public Rectangle Cell(int row, int col) => new((int)(ColumnX[col] - CellWidth / 2),
        (int)(Rows[row].Y - CellHeight / 2), (int)CellWidth, (int)CellHeight);
}

internal static class GridVision
{
    public static readonly RectangleF DefaultArea = new(.025f, .17f, .68f, .67f);

    public static GridObservation Analyze(Bitmap source, RectangleF relativeArea)
    {
        var scale = Math.Min(1d, 1440d / source.Width);
        using var reduced = new Bitmap((int)(source.Width * scale), (int)(source.Height * scale), PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(reduced))
        {
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.DrawImage(source, new Rectangle(Point.Empty, reduced.Size));
        }
        var pixels = new Pixels(reduced);
        var area = Rectangle.Intersect(new Rectangle(0, 0, reduced.Width, reduced.Height), new Rectangle(
            (int)(relativeArea.X * reduced.Width), (int)(relativeArea.Y * reduced.Height),
            (int)(relativeArea.Width * reduced.Width), (int)(relativeArea.Height * reduced.Height)));
        var blobs = Components(pixels, area);
        var components = blobs.Select(b => b.Rect).ToArray();
        // Card art and equipped portraits also contain colored U-shaped contours.
        // Establish scale from repeated horizontal rarity bars before ranking focus
        // candidates, so a 27px portrait cannot compete with a 93px card border.
        var scaleBars = components.Where(c => c.Width >= Math.Max(22, reduced.Width * .025)
            && c.Width < area.Width * .4 && c.Height >= 2 && c.Height <= c.Width * .20).ToArray();
        var stripeWidth = scaleBars.Length == 0 ? 0 : Median(scaleBars.Select(c => (double)c.Width));
        var selections = blobs.Where(b => b.Count < b.Rect.Width * b.Rect.Height * .65).Select(b => b.Rect)
            .Where(c => c.Width >= Math.Max(22, reduced.Width * .018)
                && (stripeWidth == 0 || (c.Width >= stripeWidth * .80 && c.Width <= stripeWidth * 1.50))
                && c.Width < area.Width * .45 && c.Height >= c.Width * .65 && c.Height <= c.Width * 1.6)
            // Equipped portraits and slot badges can split the thin focus border.
            // The remaining U-shaped component still anchors both sides and bottom.
            .Select(c => new[] { c, new Rectangle(c.Left, c.Bottom - c.Width, c.Width, c.Width) }
                .Select(r => (Rect: r, Score: FrameScore(pixels, r))).MaxBy(r => r.Score))
            .Where(c => c.Score > .48)
            .OrderByDescending(c => c.Score).ToArray();
        if (selections.Length == 0) throw new InvalidOperationException("未识别到彩色选中框；请进入手柄模式并选中驱动盘，或调整网格区域。");
        if (selections.Length > 1 && selections[1].Score > selections[0].Score - .15)
            throw new InvalidOperationException("存在多个相似选中框，无法确定焦点；请移开鼠标或调整网格区域。 candidates="
                + string.Join(";", selections.Take(5).Select(c => $"{c.Rect}:{c.Score:F3}")));
        var selected = selections[0].Rect;
        var stripes = components.Where(c => c.Width >= selected.Width * .52 && c.Width <= selected.Width * 1.1
            && c.Height >= 2 && c.Height <= selected.Height * .25).ToArray();
        if (stripes.Length == 0) throw new InvalidOperationException("未识别到稳定的品质条。");
        // Focus borders pulse in thickness. Anchor sampling to the stationary rarity
        // stripes instead, otherwise an idle item appears to change its row contents.
        var cellSize = Median(stripes.Select(c => (double)c.Width)) / .88;
        var bands = stripes.Select(c => new PointF(c.Left + c.Width / 2f, c.Bottom)).ToList();
        // The selection border can join the rarity stripe into a single component.
        bands.RemoveAll(p => p.X >= selected.Left && p.X <= selected.Right
            && p.Y >= selected.Top - 3 && p.Y <= selected.Bottom + 3);
        bands.Add(new PointF(selected.Left + selected.Width / 2f, selected.Bottom - selected.Height * .07f));
        var rowGroups = Cluster(bands, p => p.Y, selected.Height * .18).OrderBy(g => g.Average(p => p.Y)).ToArray();
        if (rowGroups.Length == 0) throw new InvalidOperationException("未识别到驱动盘网格。");
        var selectedRow = Array.FindIndex(rowGroups, g => g.Any(p => Math.Abs(p.X - (selected.Left + selected.Width / 2d)) < 1
            && Math.Abs(p.Y - (selected.Bottom - selected.Height * .07)) < 1));
        if (selectedRow < 0) throw new InvalidOperationException("选中框无法对应到网格行。");
        var centers = Cluster(bands, p => p.X, selected.Width * .30).Select(g => Median(g.Select(p => (double)p.X))).Order().ToArray();
        if (centers.Length > 24 || rowGroups.Length > 12) throw new InvalidOperationException("网格候选过多，可能包含其他界面区域。");
        if (centers.Length > 1)
        {
            var steps = centers.Zip(centers.Skip(1), (a, b) => b - a).ToArray();
            var pitch = Median(steps);
            if (pitch < selected.Width * .85 || pitch > selected.Width * 1.9 || steps.Any(s => Math.Abs(s - pitch) > pitch * .18))
                throw new InvalidOperationException("列间距不一致，可能漏检格子或混入其他控件。");
        }
        var rowY = rowGroups.Select(g => Median(g.Select(p => (double)p.Y))).ToArray();
        var rows = rowGroups.Select((group, index) =>
        {
            var columns = group.Select(p => Nearest(centers, p.X)).Distinct().Order().ToArray();
            if (!columns.SequenceEqual(Enumerable.Range(0, columns.Length)))
                throw new InvalidOperationException("某一行内部存在未识别的格子，拒绝把它当成不足一行。");
            var centerY = rowY[index] - cellSize * .48;
            var full = centerY - cellSize / 2d >= area.Top - 3 && centerY + cellSize / 2d <= area.Bottom + 3;
            return new GridRow(centerY / scale, columns, RowSignature(pixels, centers, centerY, (int)cellSize, (int)cellSize, columns), full);
        }).ToArray();
        if (rows.Length > 1)
        {
            var steps = rows.Zip(rows.Skip(1), (a, b) => b.Y - a.Y).ToArray();
            var pitch = Median(steps);
            if (steps.Any(s => Math.Abs(s - pitch) > pitch * .18)) throw new InvalidOperationException("行间距不一致，请调整网格区域。");
        }
        return new GridObservation(source.Width, source.Height,
            new Rectangle((int)(area.X / scale), (int)(area.Y / scale), (int)(area.Width / scale), (int)(area.Height / scale)),
            centers.Select(x => x / scale).ToArray(), rows, selectedRow,
            Nearest(centers, selected.Left + selected.Width / 2d),
            new Rectangle((int)(selected.X / scale), (int)(selected.Y / scale), (int)(selected.Width / scale), (int)(selected.Height / scale)),
            cellSize / scale, cellSize / scale);
    }

    public static double Difference(byte[] a, byte[] b) => a.Length != b.Length || a.Length == 0 ? 255
        : a.Zip(b, (x, y) => Math.Abs(x - y)).Average();

    public static bool SameLayout(GridObservation a, GridObservation b) => a.Width == b.Width && a.Height == b.Height
        && a.Columns == b.Columns && a.ColumnX.Zip(b.ColumnX, (x, y) => Math.Abs(x - y)).All(d => d < a.CellWidth * .12)
        && Math.Abs(a.CellWidth - b.CellWidth) < a.CellWidth * .12;

    public static Bitmap Overlay(Bitmap source, GridObservation grid)
    {
        var image = new Bitmap(source);
        using var graphics = Graphics.FromImage(image);
        using var line = new Pen(Color.Cyan, Math.Max(2, source.Width / 800f));
        using var focus = new Pen(Color.Magenta, Math.Max(3, source.Width / 600f));
        using var font = new Font("Segoe UI", Math.Max(12, source.Width / 130f), FontStyle.Bold);
        graphics.DrawRectangle(line, grid.SearchArea);
        for (var row = 0; row < grid.Rows.Length; row++)
            foreach (var col in grid.Rows[row].Columns)
            {
                var rect = grid.Cell(row, col);
                graphics.DrawRectangle(line, rect);
                graphics.DrawString($"{row},{col}", font, Brushes.Cyan, rect.Left, rect.Top);
            }
        graphics.DrawRectangle(focus, grid.Selection);
        return image;
    }

    private static byte[] RowSignature(Pixels pixels, double[] xs, double y, int width, int height, int[] columns)
    {
        var values = new List<byte>();
        foreach (var col in columns)
            for (var iy = 0; iy < 8; iy++)
                for (var ix = 0; ix < 8; ix++)
                {
                    var px = (int)(xs[col] + width * (-.28 + ix * .08));
                    var py = (int)(y + height * (-.23 + iy * .065));
                    // Average a small patch: single-pixel samples alias strongly when
                    // fractional scroll offsets or focus animation shift card artwork.
                    values.Add(pixels.GrayPatch(px, py, Math.Max(1, width / 35)));
                }
        return values.ToArray();
    }

    private static double FrameScore(Pixels pixels, Rectangle r)
    {
        double Edge(bool vertical, bool far)
        {
            var hits = 0;
            for (var i = 0; i < 30; i++)
            {
                var fraction = .20 + .60 * i / 29;
                var hit = false;
                for (var d = 0; d <= Math.Max(2, r.Width / 18); d++)
                {
                    var x = vertical ? (far ? r.Right - 1 - d : r.Left + d) : (int)(r.Left + r.Width * fraction);
                    var y = vertical ? (int)(r.Top + r.Height * fraction) : r.Top + d;
                    hit |= pixels.Colored(x, y);
                }
                if (hit) hits++;
            }
            return hits / 30d;
        }
        return Math.Min(Edge(true, false), Math.Min(Edge(true, true), Edge(false, false)));
    }

    private static List<(Rectangle Rect, int Count)> Components(Pixels pixels, Rectangle area)
    {
        var visited = new bool[pixels.Width * pixels.Height];
        var queue = new int[visited.Length];
        var result = new List<(Rectangle Rect, int Count)>();
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
            {
                var seed = y * pixels.Width + x;
                if (visited[seed] || !pixels.Colored(x, y)) continue;
                var head = 0; var tail = 1; queue[0] = seed; visited[seed] = true;
                var minX = x; var maxX = x; var minY = y; var maxY = y;
                while (head < tail)
                {
                    var point = queue[head++]; var cy = point / pixels.Width; var cx = point % pixels.Width;
                    minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx); minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                    Add(cx - 1, cy); Add(cx + 1, cy); Add(cx, cy - 1); Add(cx, cy + 1);
                    void Add(int nx, int ny)
                    {
                        if (!area.Contains(nx, ny)) return;
                        var p = ny * pixels.Width + nx;
                        if (visited[p]) return;
                        visited[p] = true;
                        if (pixels.Colored(nx, ny)) queue[tail++] = p;
                    }
                }
                if (tail >= 8) result.Add((Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1), tail));
            }
        return result;
    }

    private static List<List<PointF>> Cluster(IEnumerable<PointF> points, Func<PointF, double> key, double tolerance)
    {
        var groups = new List<List<PointF>>();
        foreach (var point in points.OrderBy(key))
        {
            if (groups.Count == 0 || Math.Abs(key(point) - groups[^1].Average(key)) > tolerance) groups.Add([]);
            groups[^1].Add(point);
        }
        return groups;
    }
    private static int Nearest(double[] values, double value) => Enumerable.Range(0, values.Length).MinBy(i => Math.Abs(values[i] - value));
    private static double Median(IEnumerable<double> values) { var sorted = values.Order().ToArray(); return sorted[sorted.Length / 2]; }

    private sealed class Pixels
    {
        private readonly byte[] bytes;
        private readonly int stride;
        public int Width { get; }
        public int Height { get; }
        public Pixels(Bitmap image)
        {
            Width = image.Width; Height = image.Height;
            var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try { stride = data.Stride; bytes = new byte[stride * Height]; Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); }
            finally { image.UnlockBits(data); }
        }
        public bool Colored(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
            var i = y * stride + x * 3; var b = bytes[i]; var g = bytes[i + 1]; var r = bytes[i + 2];
            return Math.Max(r, Math.Max(g, b)) >= 140 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) >= 65;
        }
        public byte Gray(int x, int y)
        {
            x = Math.Clamp(x, 0, Width - 1); y = Math.Clamp(y, 0, Height - 1);
            var i = y * stride + x * 3;
            return (byte)((bytes[i] + bytes[i + 1] * 2 + bytes[i + 2]) / 4);
        }
        public byte GrayPatch(int x, int y, int radius)
        {
            var total = 0; var count = 0;
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++) { total += Gray(x + dx, y + dy); count++; }
            return (byte)(total / count);
        }
    }
}
