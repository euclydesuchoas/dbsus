// Draws the package icon: a graphite table on a solid amber rounded square that fills the canvas.
// The table has a tall, dark header and two light row lines, the first shorter than the second.
// Original artwork: no text, no arrow, and no column line or other crossing strokes.
//
// Usage, from anywhere inside the repository:
//   dotnet run tools/icon/make-icon.cs                      writes icon.png (128 x 128) at the root
//   dotnet run tools/icon/make-icon.cs -- --preview <dir>   writes the icon at 128, 64 and 32 px
//                                                           and a sheet on light and dark
//                                                           backgrounds, plain and with the two
//                                                           status badges Visual Studio draws on
//                                                           icons (for testing only; the badges
//                                                           never go into the package icon)
//
// Only the SDK is used: the shapes are rendered with 8 x 8 supersampling and the PNG is written by
// hand, with uncompressed (stored) deflate blocks, so the output is the same bytes on every run and
// every runtime.

using System.Buffers.Binary;

const int Supersampling = 8;
var amber = new Rgba(0xF2, 0xA9, 0x3B, 0xFF);
var graphite = new Rgba(0x3B, 0x41, 0x48, 0xFF);
var header = new Rgba(0x1C, 0x1F, 0x23, 0xFF);
var light = new Rgba(0xE9, 0xEC, 0xEF, 0xFF);
var white = new Rgba(0xFF, 0xFF, 0xFF, 0xFF);
var installedGreen = new Rgba(0x2E, 0x8B, 0x3E, 0xFF);
var updateBlue = new Rgba(0x1E, 0x6F, 0xD9, 0xFF);
var backgrounds = new[] { new Rgba(0xFF, 0xFF, 0xFF, 0xFF), new Rgba(0x1E, 0x1E, 0x1E, 0xFF) };
var sizes = new[] { 128, 64, 32 };

if (args.Length == 2 && args[0] == "--preview")
{
    var directory = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(directory);
    foreach (var size in sizes)
    {
        WritePng(Path.Combine(directory, $"icon-{size}.png"), size, size, Render(size, IconAt));
    }

    WriteSheet(Path.Combine(directory, "sheet-final.png"));
    Console.WriteLine($"Previews written to {directory}");
    return 0;
}

if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: dotnet run tools/icon/make-icon.cs [-- --preview <dir>]");
    return 1;
}

var path = Path.Combine(FindRepositoryRoot(Directory.GetCurrentDirectory()), "icon.png");
WritePng(path, 128, 128, Render(128, IconAt));
Console.WriteLine($"Wrote {path}");
return 0;

// The icon's color at a point of the 128 x 128 grid, or null where it is transparent.
Rgba? IconAt(double x, double y)
{
    // The solid amber square, 2 px from the edges.
    if (!InRoundedRectangle(x, y, 2, 2, 126, 126, 22))
    {
        return null;
    }

    // The table, from x 18 to 110 and y 20 to 108.
    if (!InRoundedRectangle(x, y, 18, 20, 110, 108, 9))
    {
        return amber;
    }

    if (y < 50)
    {
        return header;
    }

    // Two light row lines; the first, about 70% as long, so that they do not read as an equals sign.
    var firstLine = y >= 62 && y <= 72 && x >= 31 && x <= 77;
    var secondLine = y >= 84 && y <= 94 && x >= 31 && x <= 97;
    return firstLine || secondLine ? light : graphite;
}

// A status badge as Visual Studio draws it over a package icon: a circle about 40% of the icon's
// width, in the bottom right corner. Testing only.
Rgba? BadgeAt(bool update, double x, double y)
{
    const double Radius = 128 * 0.2;
    const double CenterX = 128 - Radius - 1;
    const double CenterY = 128 - Radius - 1;
    var dx = (x - CenterX) / Radius;
    var dy = (y - CenterY) / Radius;
    if ((dx * dx) + (dy * dy) > 1)
    {
        return null;
    }

    var mark = update
        ? (Math.Abs(dx) <= 0.13 && dy >= -0.2 && dy <= 0.55) || InTriangle(dx, dy, (-0.45, -0.05), (0.45, -0.05), (0, -0.58))
        : NearSegment(dx, dy, (-0.45, 0.02), (-0.12, 0.35), 0.13) || NearSegment(dx, dy, (-0.12, 0.35), (0.48, -0.3), 0.13);
    return mark ? white : update ? updateBlue : installedGreen;
}

// The icon at every size, on a light and a dark background, each plain and with the "installed"
// and the "update available" badges.
void WriteSheet(string path)
{
    const int Margin = 16;
    var width = Margin + sizes.Sum(size => size + Margin);
    var rowHeight = 128 + (2 * Margin);
    var badgeKinds = new bool?[] { null, false, true };
    var rows = backgrounds.Length * badgeKinds.Length;
    var pixels = new Rgba[width * rowHeight * rows];
    var row = 0;
    foreach (var background in backgrounds)
    {
        foreach (var badge in badgeKinds)
        {
            Array.Fill(pixels, background, row * width * rowHeight, width * rowHeight);
            var left = Margin;
            foreach (var size in sizes)
            {
                var image = Render(size, IconAt);
                var overlay = badge is { } update ? Render(size, (x, y) => BadgeAt(update, x, y)) : null;
                var top = (row * rowHeight) + Margin + ((128 - size) / 2);
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var index = ((top + y) * width) + left + x;
                        pixels[index] = Over(image[(y * size) + x], pixels[index]);
                        if (overlay is not null)
                        {
                            pixels[index] = Over(overlay[(y * size) + x], pixels[index]);
                        }
                    }
                }

                left += size + Margin;
            }

            row++;
        }
    }

    WritePng(path, width, rowHeight * rows, pixels);
}

// Renders a layer at size x size from a function on the 128 x 128 grid.
static Rgba[] Render(int size, Func<double, double, Rgba?> colorAt)
{
    var pixels = new Rgba[size * size];
    var scale = 128.0 / size;
    for (var y = 0; y < size; y++)
    {
        for (var x = 0; x < size; x++)
        {
            double r = 0, g = 0, b = 0;
            var covered = 0;
            for (var sy = 0; sy < Supersampling; sy++)
            {
                for (var sx = 0; sx < Supersampling; sx++)
                {
                    if (colorAt((x + ((sx + 0.5) / Supersampling)) * scale, (y + ((sy + 0.5) / Supersampling)) * scale) is { } color)
                    {
                        r += color.R;
                        g += color.G;
                        b += color.B;
                        covered++;
                    }
                }
            }

            // Every shape is opaque, so the average color of the covered samples is exact.
            pixels[(y * size) + x] = covered == 0
                ? default
                : new Rgba(
                    (byte)Math.Round(r / covered),
                    (byte)Math.Round(g / covered),
                    (byte)Math.Round(b / covered),
                    (byte)Math.Round(255.0 * covered / (Supersampling * Supersampling)));
        }
    }

    return pixels;
}

static bool InRoundedRectangle(double x, double y, double left, double top, double right, double bottom, double radius)
{
    if (x < left || x > right || y < top || y > bottom)
    {
        return false;
    }

    var cx = Math.Clamp(x, left + radius, right - radius);
    var cy = Math.Clamp(y, top + radius, bottom - radius);
    return ((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= radius * radius;
}

static bool InTriangle(double x, double y, (double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
{
    static double Side(double px, double py, (double X, double Y) p, (double X, double Y) q) =>
        ((px - q.X) * (p.Y - q.Y)) - ((p.X - q.X) * (py - q.Y));

    var d1 = Side(x, y, a, b);
    var d2 = Side(x, y, b, c);
    var d3 = Side(x, y, c, a);
    var negative = d1 < 0 || d2 < 0 || d3 < 0;
    var positive = d1 > 0 || d2 > 0 || d3 > 0;
    return !(negative && positive);
}

static bool NearSegment(double x, double y, (double X, double Y) a, (double X, double Y) b, double halfWidth)
{
    var abX = b.X - a.X;
    var abY = b.Y - a.Y;
    var t = Math.Clamp((((x - a.X) * abX) + ((y - a.Y) * abY)) / ((abX * abX) + (abY * abY)), 0, 1);
    var dx = x - (a.X + (t * abX));
    var dy = y - (a.Y + (t * abY));
    return (dx * dx) + (dy * dy) <= halfWidth * halfWidth;
}

static Rgba Over(Rgba top, Rgba bottom)
{
    var alpha = top.A / 255.0;
    return new Rgba(
        (byte)Math.Round((top.R * alpha) + (bottom.R * (1 - alpha))),
        (byte)Math.Round((top.G * alpha) + (bottom.G * (1 - alpha))),
        (byte)Math.Round((top.B * alpha) + (bottom.B * (1 - alpha))),
        0xFF);
}

// A PNG with 8-bit RGBA pixels, no filtering, and zlib data made of stored deflate blocks.
static void WritePng(string path, int width, int height, Rgba[] pixels)
{
    var raw = new byte[height * ((width * 4) + 1)];
    var offset = 0;
    for (var y = 0; y < height; y++)
    {
        raw[offset++] = 0;
        for (var x = 0; x < width; x++)
        {
            var pixel = pixels[(y * width) + x];
            raw[offset++] = pixel.R;
            raw[offset++] = pixel.G;
            raw[offset++] = pixel.B;
            raw[offset++] = pixel.A;
        }
    }

    using var file = File.Create(path);
    file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

    var header = new byte[13];
    BinaryPrimitives.WriteInt32BigEndian(header, width);
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
    header[8] = 8;
    header[9] = 6;
    WriteChunk(file, "IHDR", header);
    WriteChunk(file, "IDAT", Zlib(raw));
    WriteChunk(file, "IEND", []);
}

static byte[] Zlib(byte[] data)
{
    const int MaxBlock = 65535;
    using var output = new MemoryStream();
    output.WriteByte(0x78);
    output.WriteByte(0x01);
    for (var start = 0; start < data.Length; start += MaxBlock)
    {
        var length = Math.Min(MaxBlock, data.Length - start);
        output.WriteByte(start + length >= data.Length ? (byte)1 : (byte)0);
        output.WriteByte((byte)length);
        output.WriteByte((byte)(length >> 8));
        output.WriteByte((byte)~length);
        output.WriteByte((byte)(~length >> 8));
        output.Write(data, start, length);
    }

    uint s1 = 1, s2 = 0;
    foreach (var value in data)
    {
        s1 = (s1 + value) % 65521;
        s2 = (s2 + s1) % 65521;
    }

    var adler = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(adler, (s2 << 16) | s1);
    output.Write(adler);
    return output.ToArray();
}

static void WriteChunk(Stream stream, string type, byte[] data)
{
    var length = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
    stream.Write(length);
    var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
    stream.Write(typeBytes);
    stream.Write(data);
    var crc = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32([.. typeBytes, .. data]));
    stream.Write(crc);
}

static uint Crc32(byte[] data)
{
    var crc = 0xFFFFFFFFu;
    foreach (var value in data)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
    }

    return ~crc;
}

static string FindRepositoryRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "DbSus.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException("Run this from inside the DbSus repository.");
}

internal readonly record struct Rgba(byte R, byte G, byte B, byte A);
