using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var logosDir = args[0];
const int canvas = 256;
var factors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
{
    // Steam solid disc — match peer diameter (0.70 was tiny on cards; keep ~212px)
    ["steam.png"] = 1.06,
    ["epic.png"] = 0.84,
    ["amd.png"] = 1.14,
    ["nvidia.png"] = 1.05,
    ["discord.png"] = 1.00,
    ["internet.png"] = 1.02,
    ["windows.png"] = 0.96,
    ["brave.png"] = 1.00,
    ["riot.png"] = 1.00,
};
const double baseMax = 200;

static Bitmap LoadUnlocked(string path)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var tmp = Image.FromStream(fs);
    var bmp = new Bitmap(tmp.Width, tmp.Height, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.Clear(Color.Transparent);
    g.DrawImage(tmp, 0, 0, tmp.Width, tmp.Height);
    return bmp;
}

var processed = new List<(string name, Bitmap bmp)>();
foreach (var f in Directory.GetFiles(logosDir, "*.png").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
{
    var name = Path.GetFileName(f);
    if (name.Equals("optihub.png", StringComparison.OrdinalIgnoreCase)) continue;
    if (name.Contains("discord-white", StringComparison.OrdinalIgnoreCase)) continue;
    if (name.StartsWith("_", StringComparison.Ordinal)) continue;
    if (name.Contains(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

    using var argb = LoadUnlocked(f);
    int minX = argb.Width, minY = argb.Height, maxX = -1, maxY = -1;
    for (int y = 0; y < argb.Height; y++)
    for (int x = 0; x < argb.Width; x++)
    {
        if (argb.GetPixel(x, y).A <= 18) continue;
        if (x < minX) minX = x; if (x > maxX) maxX = x;
        if (y < minY) minY = y; if (y > maxY) maxY = y;
    }
    if (maxX < 0) { Console.WriteLine("skip empty " + name); continue; }
    int bw = maxX - minX + 1, bh = maxY - minY + 1;
    using var crop = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
    using (var gc = Graphics.FromImage(crop))
    {
        gc.Clear(Color.Transparent);
        gc.DrawImage(argb, new Rectangle(0, 0, bw, bh), new Rectangle(minX, minY, bw, bh), GraphicsUnit.Pixel);
    }

    double factor = factors.TryGetValue(name, out var ff) ? ff : 1.0;
    double tMax = baseMax * factor;
    double scale = bw > bh * 1.8 ? tMax / bw : tMax / Math.Max(bw, bh);
    int nw = Math.Max(1, (int)Math.Round(bw * scale));
    int nh = Math.Max(1, (int)Math.Round(bh * scale));
    if (nw > canvas - 16) { var s = (canvas - 16.0) / nw; nw = canvas - 16; nh = Math.Max(1, (int)Math.Round(nh * s)); }
    if (nh > canvas - 16) { var s = (canvas - 16.0) / nh; nh = canvas - 16; nw = Math.Max(1, (int)Math.Round(nw * s)); }

    using var scaled = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
    using (var gs = Graphics.FromImage(scaled))
    {
        gs.Clear(Color.Transparent);
        gs.InterpolationMode = InterpolationMode.HighQualityBicubic;
        gs.PixelOffsetMode = PixelOffsetMode.HighQuality;
        gs.SmoothingMode = SmoothingMode.HighQuality;
        gs.DrawImage(crop, new Rectangle(0, 0, nw, nh));
    }

    var outBmp = new Bitmap(canvas, canvas, PixelFormat.Format32bppArgb);
    using (var go = Graphics.FromImage(outBmp))
    {
        go.Clear(Color.Transparent);
        go.InterpolationMode = InterpolationMode.HighQualityBicubic;
        go.DrawImage(scaled, (canvas - nw) / 2, (canvas - nh) / 2, nw, nh);
    }
    var tmp = f + ".tmp.png";
    outBmp.Save(tmp, ImageFormat.Png);
    File.Delete(f);
    File.Move(tmp, f);
    Console.WriteLine($"{name,-14} -> {nw}x{nh}  f={factor:0.00}");
    processed.Add((name, outBmp));
}

// contact sheet
if (processed.Count > 0)
{
    const int cell = 168; const int pad = 18;
    var cols = 4;
    var rows = (int)Math.Ceiling(processed.Count / (double)cols);
    using var sheet = new Bitmap(cols * (cell + pad) + pad, rows * (cell + pad + 30) + pad);
    using var g = Graphics.FromImage(sheet);
    g.Clear(Color.FromArgb(255, 6, 6, 6));
    using var font = new Font("Segoe UI", 11f);
    using var brush = new SolidBrush(Color.FromArgb(200, 200, 200));
    for (var i = 0; i < processed.Count; i++)
    {
        var (name, bmp) = processed[i];
        var col = i % cols; var row = i / cols;
        var x = pad + col * (cell + pad);
        var y = pad + row * (cell + pad + 30);
        using var cellBg = new Bitmap(cell, cell);
        using (var gt = Graphics.FromImage(cellBg))
        {
            gt.Clear(Color.FromArgb(255, 16, 16, 16));
            gt.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // Same on-card scale: ~68/188 of card ≈ 36% of cell… use 72% of cell like UI logo well
            var s = (int)(cell * 0.72);
            gt.DrawImage(bmp, (cell - s) / 2, (cell - s) / 2, s, s);
        }
        g.DrawImage(cellBg, x, y);
        g.DrawString(Path.GetFileNameWithoutExtension(name), font, brush, x, y + cell + 6);
    }
    var contactPath = Path.Combine(logosDir, "_contact.png");
    sheet.Save(contactPath, ImageFormat.Png);
    Console.WriteLine("Contact: " + contactPath);
}
foreach (var (_, b) in processed) b.Dispose();
Console.WriteLine("Done.");
