using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AppleDrive.Testing;

public enum TestImageFormat
{
    Jpeg,
    Png,
}

/// <summary>
/// Synthetic photographs for perceptual-hash tests: a smooth two-colour gradient with a few soft
/// shapes, defined in normalised coordinates so the same picture renders at any size. Different
/// seeds give different pictures.
/// </summary>
public static class TestImages
{
    /// <param name="seed">Which picture.</param>
    /// <param name="quality">JPEG quality, 0–1.</param>
    /// <param name="modified">Paints a small patch over the picture (a slight edit).</param>
    /// <param name="quarterTurns">Stores the picture rotated clockwise, as a phone thumbnail is.</param>
    /// <param name="mirrored">Stores the picture mirrored left to right, as a front-camera thumbnail is.</param>
    public static byte[] Render(
        int seed,
        int width = 800,
        int height = 600,
        TestImageFormat format = TestImageFormat.Jpeg,
        double quality = 0.9,
        bool modified = false,
        int quarterTurns = 0,
        bool mirrored = false)
    {
        var pixels = Draw(seed, width, height, modified);
        for (var turn = 0; turn < quarterTurns % 4; turn++)
        {
            (pixels, width, height) = (RotateClockwise(pixels, width, height), height, width);
        }

        if (mirrored)
        {
            pixels = Mirror(pixels, width, height);
        }

        return EncodeAsync(pixels, width, height, format, quality).GetAwaiter().GetResult();
    }

    private static byte[] Draw(int seed, int width, int height, bool modified)
    {
        var random = new Random(seed);
        var top = Color(random);
        var bottom = Color(random);
        var shapes = Enumerable.Range(0, 6)
            .Select(_ => (X: random.NextDouble(), Y: random.NextDouble(), Radius: 0.08 + (random.NextDouble() * 0.25), Color: Color(random)))
            .ToList();

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var v = (y + 0.5) / height;
            for (var x = 0; x < width; x++)
            {
                var u = (x + 0.5) / width;
                double r = Lerp(top.R, bottom.R, v), g = Lerp(top.G, bottom.G, v), b = Lerp(top.B, bottom.B, v);
                foreach (var shape in shapes)
                {
                    var distance = Math.Sqrt(((u - shape.X) * (u - shape.X)) + ((v - shape.Y) * (v - shape.Y)));
                    var alpha = Math.Clamp((shape.Radius - distance) / 0.02, 0, 1) * 0.85;
                    r = Lerp(r, shape.Color.R, alpha);
                    g = Lerp(g, shape.Color.G, alpha);
                    b = Lerp(b, shape.Color.B, alpha);
                }

                if (modified && u is > 0.70 and < 0.82 && v is > 0.72 and < 0.84)
                {
                    r = g = b = 250;
                }

                var offset = ((y * width) + x) * 4;
                pixels[offset] = (byte)b;
                pixels[offset + 1] = (byte)g;
                pixels[offset + 2] = (byte)r;
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }

    private static (double R, double G, double B) Color(Random random) =>
        (random.Next(20, 236), random.Next(20, 236), random.Next(20, 236));

    private static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);

    private static byte[] RotateClockwise(byte[] pixels, int width, int height)
    {
        var rotated = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // (x, y) moves to (height - 1 - y, x) in an image that is `height` wide.
                var target = ((x * height) + (height - 1 - y)) * 4;
                Array.Copy(pixels, ((y * width) + x) * 4, rotated, target, 4);
            }
        }

        return rotated;
    }

    private static byte[] Mirror(byte[] pixels, int width, int height)
    {
        var mirrored = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Array.Copy(pixels, ((y * width) + x) * 4, mirrored, ((y * width) + (width - 1 - x)) * 4, 4);
            }
        }

        return mirrored;
    }

    private static async Task<byte[]> EncodeAsync(byte[] pixels, int width, int height, TestImageFormat format, double quality)
    {
        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder;
        if (format == TestImageFormat.Jpeg)
        {
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) };
            encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
        }
        else
        {
            encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        }

        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();

        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}
