using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using FluxBrowser.Browser;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FluxBrowser;

/// <summary>
/// Acrylic for menus and panels over a WebView2 page. XAML cannot blur another process's pixels, so when such a surface opens the
/// page is captured once, shrunk, blurred and tinted, and that picture (not a live blur) is the surface's background, positioned so it
/// shows exactly what lies behind the surface. The page is frozen for the second or so a menu is open, which is not noticeable;
/// in exchange the material is exact at any scale, costs nothing per frame, and cannot drift. Until the capture arrives (about a tenth
/// of a second) the surface has a nearly solid fill.
/// </summary>
public sealed partial class MainWindow
{
    private ImageSource? _glassImage;
    private Size _glassPage;
    private long _glassTick;
    private bool _glassBusy;
    private int _glassToken;

    private bool OverWebView => WebEngine.UseWebView2 && !ViewportLayout.IsInternal(_currentAddress);

    private async Task ApplyAcrylicAsync()
    {
        if (!OverWebView || _activeTab?.SessionIfCreated is not { } session || _closing) return;
        // A capture taken a moment ago (menu opened over the panel, say) is still what is behind the new surface.
        if (_glassImage is not null && Environment.TickCount64 - _glassTick < 600) { RefreshAcrylic(); return; }
        if (_glassBusy) return;
        _glassBusy = true;
        int token = ++_glassToken;
        try
        {
            bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
            Windows.UI.Color tint = dark ? Windows.UI.Color.FromArgb(255, 36, 36, 36) : Windows.UI.Color.FromArgb(255, 247, 247, 247);
            double inset = _activeTab?.TopInset ?? 0;
            byte[] png = await session.CaptureScreenshotAsync();
            (byte[] pixels, int width, int height) = await DecodeAndBlurAsync(png, 300, tint, Math.Clamp(Preferences.MenuOpacity - .2, .45, .85));
            if (token != _glassToken || _closing) return;
            var bitmap = new WriteableBitmap(width, height);
            using (Stream stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
            bitmap.Invalidate();
            _glassImage = bitmap;
            _glassTick = Environment.TickCount64;
            // Where the picture sits: the web view's size (the page area minus the bars when it starts below them).
            _glassPage = new Size(PageGrid.ActualWidth, Math.Max(1, PageGrid.ActualHeight - inset));
            _glassInset = inset;
            RefreshAcrylic();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or ArgumentException)
        {
            Diagnostics.AppLog.Warning("No se pudo preparar el acrílico", e);
        }
        finally { _glassBusy = false; }
    }

    private double _glassInset;

    private void RefreshAcrylic()
    {
        if (_glassImage is null || !OverWebView) return;
        if (MenuBorder.Visibility == Visibility.Visible && _menuOpen) ApplyGlassBrush(MenuBorder);
        if (ManagementPanel.Visibility == Visibility.Visible && _panelOpen) ApplyGlassBrush(ManagementPanel);
    }

    /// <summary>Makes the captured picture the surface's background, scaled and shifted so it lines up with the page behind it.</summary>
    private void ApplyGlassBrush(Border target)
    {
        double width = target.ActualWidth, height = target.ActualHeight;
        if (width <= 1 || height <= 1 || _glassImage is null) return;
        // Where the surface settles (not where its opening animation currently has it).
        double x = target == MenuBorder ? target.Margin.Left : PageGrid.ActualWidth - target.Margin.Right - width;
        double y = target.Margin.Top - _glassInset;
        target.Background = new ImageBrush
        {
            ImageSource = _glassImage, Stretch = Stretch.Fill,
            RelativeTransform = new CompositeTransform
            {
                ScaleX = _glassPage.Width / width, ScaleY = _glassPage.Height / height,
                TranslateX = -x / width, TranslateY = -y / height
            }
        };
    }

    private static async Task<(byte[] Pixels, int Width, int Height)> DecodeAndBlurAsync(byte[] png, int targetWidth, Windows.UI.Color tint, double tintAmount)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        uint width = (uint)targetWidth, height = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * (double)targetWidth / decoder.PixelWidth));
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
        PixelDataProvider data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] pixels = data.DetachPixelData();
        await Task.Run(() =>
        {
            BoxBlur(pixels, (int)width, (int)height, 3, 3);
            double keep = 1 - tintAmount;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = (byte)(pixels[i] * keep + tint.B * tintAmount);
                pixels[i + 1] = (byte)(pixels[i + 1] * keep + tint.G * tintAmount);
                pixels[i + 2] = (byte)(pixels[i + 2] * keep + tint.R * tintAmount);
                pixels[i + 3] = 255;
            }
        });
        return (pixels, (int)width, (int)height);
    }

    /// <summary>Repeated box blur (three passes approximate a Gaussian) on BGRA pixels: along the rows, then along the columns, with running sums.</summary>
    private static void BoxBlur(byte[] pixels, int width, int height, int radius, int passes)
    {
        var scratch = new byte[pixels.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            BlurAxis(pixels, scratch, height, width, 4, width * 4, radius);
            BlurAxis(scratch, pixels, width, height, width * 4, 4, radius);
        }
    }

    /// <param name="count">How many lines (rows or columns).</param>
    /// <param name="span">Pixels along each line.</param>
    /// <param name="step">Bytes between neighbouring pixels of a line.</param>
    /// <param name="lineStride">Bytes between the starts of consecutive lines.</param>
    private static void BlurAxis(byte[] source, byte[] target, int count, int span, int step, int lineStride, int radius)
    {
        int window = radius * 2 + 1;
        for (int line = 0; line < count; line++)
        {
            int origin = line * lineStride;
            for (int channel = 0; channel < 3; channel++)
            {
                int sum = 0;
                for (int k = -radius; k <= radius; k++) sum += source[origin + Math.Clamp(k, 0, span - 1) * step + channel];
                for (int p = 0; p < span; p++)
                {
                    target[origin + p * step + channel] = (byte)(sum / window);
                    int leaving = Math.Clamp(p - radius, 0, span - 1), entering = Math.Clamp(p + radius + 1, 0, span - 1);
                    sum += source[origin + entering * step + channel] - source[origin + leaving * step + channel];
                }
            }
        }
    }
}
