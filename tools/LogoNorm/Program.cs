using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var logosDir = args[0];
const int canvas = 256;
// Shared "mark diameter" — solid plates get less; open icons / wordmarks get a bit more.
var factors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
{
    ["steam.png"] = 0.78,   // solid white disc — was dominating the grid
    ["epic.png"] = 0.86,    // solid white shield
    ["amd.png"] = 1.14,     // thin wordmark — needs more width to read
    ["nvidia.png"] = 1.06,  // was over-padded
    ["discord.png"] = 1.00,
    ["internet.png"] = 0.96,
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

foreach (var f in Directory.GetFiles(logosDir, "*.png").OrderBy(x => x))
{
    var name = Path.GetFileName(f);
    if (name.Equals("optihub.png", StringComparison.OrdinalIgnoreCase)) continue;
    if (name.Contains("discord-white", StringComparison.OrdinalIgnoreCase)) continue;

    using var argb = LoadUnlocked(f);
    int minX = argb.Width, minY = argb.Height, maxX = -1, maxY = -1;
    for (int y = 0; y < argb.Height; y++)
    for (int x = 0; x < argb.Width; x++)
    {
        if (argb.GetPixel(x, y).A <= 18) continue;
        if (x < minX) minX = x; if (x > maxX) maxX = x;
        if (y < minY) minY = y; if (y > maxY) maxY = y;
    }
    if (maxX < 0) continue;
    int bw = maxX - minX + 1, bh = maxY - minY + 1;
    using var crop = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
    using (var gc = Graphics.FromImage(crop))
    {
        gc.Clear(Color.Transparent);
        gc.DrawImage(argb, new Rectangle(0, 0, bw, bh), new Rectangle(minX, minY, bw, bh), GraphicsUnit.Pixel);
    }

    double factor = factors.TryGetValue(name, out var ff) ? ff : 1.0;
    double tMax = baseMax * factor;

    // Very wide marks: fit width to tMax (AMD)
    // Square-ish marks: fit longest side to tMax
    double scale = (bw > bh * 1.8)
        ? tMax / bw
        : tMax / Math.Max(bw, bh);

    int nw = Math.Max(1, (int)Math.Round(bw * scale));
    int nh = Math.Max(1, (int)Math.Round(bh * scale));
    if (nw > canvas - 12) { var s = (canvas - 12.0) / nw; nw = canvas - 12; nh = Math.Max(1, (int)Math.Round(nh * s)); }
    if (nh > canvas - 12) { var s = (canvas - 12.0) / nh; nh = canvas - 12; nw = Math.Max(1, (int)Math.Round(nw * s)); }

    using var scaled = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
    using (var gs = Graphics.FromImage(scaled))
    {
        gs.Clear(Color.Transparent);
        gs.CompositingQuality = CompositingQuality.HighQuality;
        gs.InterpolationMode = InterpolationMode.HighQualityBicubic;
        gs.PixelOffsetMode = PixelOffsetMode.HighQuality;
        gs.SmoothingMode = SmoothingMode.HighQuality;
        gs.DrawImage(crop, new Rectangle(0, 0, nw, nh));
    }
    using var outBmp = new Bitmap(canvas, canvas, PixelFormat.Format32bppArgb);
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
    Console.WriteLine($"{name,-14} -> {nw}x{nh}  factor={factor:0.00} tMax={tMax:0}");
}
Console.WriteLine("Done.");
