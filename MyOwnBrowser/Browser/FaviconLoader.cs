using System.Diagnostics;
using System.Net.Http;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MyOwnBrowser.Browser;

internal static class FaviconLoader
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // Many sites (Wikipedia among them) refuse requests that do not say what is asking: without this their icons never load.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MyOwnBrowser-favicon");
        client.DefaultRequestHeaders.Accept.ParseAdd("image/*,*/*;q=0.5");
        return client;
    }
    private static readonly Dictionary<string, Task<byte[]>> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Chooses the icon address to fetch. A page-declared icon on a private or loopback host is ignored unless the page itself is
    /// on that host, so a public site cannot make the browser process probe the user's local network.
    /// </summary>
    public static string? Address(string? icon, string? page = null)
    {
        if (WebAddress.IsHttp(icon) && (!WebAddress.IsLocalOrPrivateHost(new Uri(icon!).Host) || SameHost(icon, page))) return icon;
        return WebAddress.IsHttp(page) ? new Uri(new Uri(page!), "/favicon.ico").AbsoluteUri : null;
    }

    private static bool SameHost(string? a, string? b) =>
        WebAddress.IsHttp(a) && WebAddress.IsHttp(b) && new Uri(a!).Host.Equals(new Uri(b!).Host, StringComparison.OrdinalIgnoreCase);

    public static async Task<ImageSource?> LoadAsync(string? address)
    {
        if (!WebAddress.IsHttp(address)) return null;
        try
        {
            if (!Cache.TryGetValue(address!, out Task<byte[]>? pending))
            {
                if (Cache.Count >= 256) Cache.Clear();
                Cache[address!] = pending = FetchAsync(address!);
            }
            byte[] data = await pending;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(data);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            if (data.Length > 4 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0)
                return await DecodeIconAsync(data);
            string start = System.Text.Encoding.UTF8.GetString(data.AsSpan(0, Math.Min(data.Length, 512)));
            if (start.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            {
                var svg = new SvgImageSource { RasterizePixelWidth = 64, RasterizePixelHeight = 64 };
                if (await svg.SetSourceAsync(stream) != SvgImageSourceLoadStatus.Success)
                    throw new InvalidDataException("El favicon SVG no se pudo decodificar.");
                return svg;
            }
            var bitmap = new BitmapImage { DecodePixelWidth = 64 };
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception e)
        {
            Cache.Remove(address!);
            Debug.WriteLine($"No se pudo cargar el favicon {address}: {e.Message}");
            return null;
        }
    }

    private static async Task<byte[]> FetchAsync(string address)
    {
        using HttpResponseMessage response = await Client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("El favicon supera 4 MB.");
        await using Stream input = await response.Content.ReadAsStreamAsync();
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            if (output.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("El favicon supera 4 MB.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task<ImageSource> DecodeIconAsync(byte[] data)
    {
        // ICO can contain PNGs or bottom-up DIBs; XAML's URI bitmap icons do not handle all website ICOs.
        if (data.Length < 6) throw new InvalidDataException("ICO incompleto.");
        int count = BitConverter.ToUInt16(data, 4);
        if (count < 1 || 6 + count * 16 > data.Length) throw new InvalidDataException("Directorio ICO no válido.");
        var entries = Enumerable.Range(0, count).Select(i =>
        {
            int p = 6 + i * 16;
            return (Width: data[p] == 0 ? 256 : data[p], Depth: BitConverter.ToUInt16(data, p + 6),
                Length: BitConverter.ToInt32(data, p + 8), Offset: BitConverter.ToInt32(data, p + 12));
        }).OrderBy(e => Math.Abs(e.Width - 32)).ThenByDescending(e => e.Depth);
        foreach (var entry in entries)
        {
            if (entry.Offset < 0 || entry.Length < 8 || entry.Offset > data.Length - entry.Length) continue;
            byte[] image = data.AsSpan(entry.Offset, entry.Length).ToArray();
            if (image[0] == 137 && image[1] == 80)
            {
                using var png = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(png.GetOutputStreamAt(0))) { writer.WriteBytes(image); await writer.StoreAsync(); }
                png.Seek(0);
                var bitmap = new BitmapImage { DecodePixelWidth = 64 };
                await bitmap.SetSourceAsync(png);
                return bitmap;
            }
            if (image.Length < 40 || BitConverter.ToInt32(image, 0) != 40) continue;
            int w = BitConverter.ToInt32(image, 4), h = BitConverter.ToInt32(image, 8) / 2;
            int bpp = BitConverter.ToUInt16(image, 14), compression = BitConverter.ToInt32(image, 16);
            if (w is < 1 or > 256 || h is < 1 or > 256 || bpp is not (32 or 24) || compression != 0) continue;
            int stride = ((w * bpp + 31) / 32) * 4, maskStride = ((w + 31) / 32) * 4;
            if (40 + stride * h > image.Length) continue;
            byte[] pixels = new byte[w * h * 4];
            bool alpha = bpp == 32 && Enumerable.Range(0, h).Any(y =>
                Enumerable.Range(0, w).Any(x => image[40 + y * stride + x * 4 + 3] != 0));
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int source = 40 + (h - y - 1) * stride + x * (bpp / 8), target = (y * w + x) * 4;
                    Array.Copy(image, source, pixels, target, 3);
                    int mask = 40 + stride * h + (h - y - 1) * maskStride + x / 8;
                    bool transparent = mask < image.Length && (image[mask] & (128 >> (x % 8))) != 0;
                    pixels[target + 3] = transparent ? (byte)0 : alpha ? image[source + 3] : (byte)255;
                }
            using var encoded = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)w, (uint)h, 96, 96, pixels);
            await encoder.FlushAsync();
            encoded.Seek(0);
            var result = new BitmapImage();
            await result.SetSourceAsync(encoded);
            return result;
        }
        return await DecodeWithSystemCodecAsync(data);
    }

    // Palettised (1/4/8-bit) and other rarely used ICO layouts: let the Windows imaging codec decode the closest-sized frame.
    private static async Task<ImageSource> DecodeWithSystemCodecAsync(byte[] data)
    {
        using var source = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(source.GetOutputStreamAt(0))) { writer.WriteBytes(data); await writer.StoreAsync(); }
        source.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.IcoDecoderId, source);
        BitmapFrame? best = null;
        for (uint i = 0; i < Math.Min(decoder.FrameCount, 16u); i++)
        {
            BitmapFrame frame = await decoder.GetFrameAsync(i);
            if (best is null || Math.Abs((int)frame.PixelWidth - 32) < Math.Abs((int)best.PixelWidth - 32)) best = frame;
        }
        if (best is null) throw new InvalidDataException("El icono no contiene imágenes.");
        using SoftwareBitmap bitmap = await best.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
        using var encoded = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        encoded.Seek(0);
        var result = new BitmapImage();
        await result.SetSourceAsync(encoded);
        return result;
    }
}
