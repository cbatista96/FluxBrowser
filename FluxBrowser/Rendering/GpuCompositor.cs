using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WinUI;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DInterpolationMode = Vortice.Direct2D1.InterpolationMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using DxgiAlphaMode = Vortice.DXGI.AlphaMode;
using WinUiSwapChainPanelNative = Vortice.WinUI.ISwapChainPanelNative;

namespace FluxBrowser.Rendering;

internal sealed class GpuCompositor : IDisposable
{
    private readonly object _gate = new();
    private readonly object _copyWaitGate = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11Device1 _sharedDevice;
    private readonly ID3D11DeviceContext _immediate;
    private readonly ID3D11DeviceContext4 _fenceContext;
    private readonly ID3D11Fence _copyFence;
    private readonly AutoResetEvent _copyFinished = new(false);
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext _d2d;
    private readonly IDXGISwapChain2 _swapChain;
    private readonly WinUiSwapChainPanelNative _panelNative;
    private readonly GaussianBlur _blur;
    private readonly ID2D1SolidColorBrush _glassTint;
    private readonly ID2D1SolidColorBrush _bookmarkTint;
    private ID2D1Bitmap1? _target;
    private ID2D1Bitmap1? _scene;
    private readonly HashSet<FrameSource> _sources = [];
    private readonly AutoResetEvent _renderPending = new(false);
    private readonly Thread _renderThread;
    private readonly ID2D1SolidColorBrush _menuTint;
    private readonly ID2D1Bitmap1 _noiseBitmap;
    private readonly ID2D1BitmapBrush _noiseBrush;
    private FrameSource? _active;
    private GlassRegion[] _regions = [];
    private readonly List<ID2D1RoundedRectangleGeometry> _regionMasks = [];
    private readonly ID2D1Layer _menuLayer;
    private float _blurRadius = 12;
    private bool _dark;
    private volatile bool _stopping;
    private bool _dirty;
    private long _presentations;
    private double _drawMilliseconds;
    private bool _disposed;
    private uint _width = 1;
    private uint _height = 1;
    private float _scale = 1;
    private float _toolbarHeight = 96;
    private ulong _fenceValue;

    public GpuCompositor(SwapChainPanel panel)
    {
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out ID3D11Device? device).CheckError();
        _device = device ?? throw new InvalidOperationException("No se pudo crear el dispositivo Direct3D.");
        _sharedDevice = _device.QueryInterface<ID3D11Device1>();
        _immediate = _device.ImmediateContext;
        _fenceContext = _immediate.QueryInterface<ID3D11DeviceContext4>();
        using ID3D11Device5 fenceDevice = _device.QueryInterface<ID3D11Device5>();
        _copyFence = fenceDevice.CreateFence(0);

        using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 dxgiFactory = adapter.GetParent<IDXGIFactory2>();
        var description = new SwapChainDescription1(
            1,
            1,
            Format.B8G8R8A8_UNorm,
            bufferCount: 2,
            scaling: Scaling.Stretch,
            swapEffect: SwapEffect.FlipSequential,
            alphaMode: DxgiAlphaMode.Premultiplied);
        using IDXGISwapChain1 swapChain = dxgiFactory.CreateSwapChainForComposition(_device, description);
        _swapChain = swapChain.QueryInterface<IDXGISwapChain2>();
        _panelNative = new WinUiSwapChainPanelNative(panel);
        _panelNative.SetSwapChain(_swapChain).CheckError();

        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
        _d2dDevice = _factory.CreateDevice(dxgiDevice);
        _d2d = _d2dDevice.CreateDeviceContext();
        _menuLayer = _d2d.CreateLayer();
        _blur = new GaussianBlur(_d2d)
        {
            StandardDeviation = 12,
            BorderMode = BorderMode.Hard
        };
        _glassTint = _d2d.CreateSolidColorBrush(new Color4(0.94f, 0.97f, 0.99f, 0.50f));
        _bookmarkTint = _d2d.CreateSolidColorBrush(new Color4(0.98f, 0.99f, 1.0f, 0.12f));
        _menuTint = _d2d.CreateSolidColorBrush(new Color4(0.94f, 0.97f, 0.99f, 0.76f));
        var pixels = new int[16 * 16];
        var random = new Random(971);
        for (int i = 0; i < pixels.Length; i++)
        {
            int gray = random.Next(256);
            pixels[i] = unchecked((int)0xFF000000) | (gray << 16) | (gray << 8) | gray;
        }
        IntPtr noiseData = Marshal.AllocHGlobal(pixels.Length * sizeof(int));
        try
        {
            Marshal.Copy(pixels, 0, noiseData, pixels.Length);
            _noiseBitmap = _d2d.CreateBitmap(new SizeI(16, 16), noiseData, 16 * sizeof(int),
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied)));
        }
        finally { Marshal.FreeHGlobal(noiseData); }
        _noiseBrush = ((ID2D1RenderTarget)_d2d).CreateBitmapBrush(_noiseBitmap);
        _noiseBrush.ExtendModeX = ExtendMode.Wrap;
        _noiseBrush.ExtendModeY = ExtendMode.Wrap;
        _noiseBrush.Opacity = 0.025f;
        CreateTarget();
        _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "Browser GPU presentation" };
        _renderThread.Start();
    }

    public event Action<Exception>? RenderingFailed;
    public readonly record struct GlassRegion(double X, double Y, double Width, double Height, float Opacity, float Visibility = 1);
    public readonly record struct Performance(long Frames, long Copies, long Presentations, double CopyTotalMilliseconds, double DrawTotalMilliseconds)
    {
        public double CopyMilliseconds => Copies > 0 ? CopyTotalMilliseconds / Copies : 0;
        public double DrawMilliseconds => Presentations > 0 ? DrawTotalMilliseconds / Presentations : 0;
    }

    public FrameSource CreateSource()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var source = new FrameSource(this);
            _sources.Add(source);
            return source;
        }
    }

    public void Select(FrameSource source)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_sources.Contains(source)) throw new InvalidOperationException("La pestaña ya no tiene superficie gráfica.");
            _active = source;
            RequestDraw();
        }
    }

    public void Configure(float blurRadius, float opacity, bool dark, GlassRegion[] regions)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _blurRadius = blurRadius;
            _dark = dark;
            if (!_regions.SequenceEqual(regions))
            {
                _regions = regions;
                RebuildRegionMasks();
            }
            _blur.StandardDeviation = blurRadius * _scale;
            _glassTint.Color = dark ? new Color4(.14f, .14f, .14f, opacity) : new Color4(.96f, .96f, .96f, opacity);
            _bookmarkTint.Color = new Color4(0, 0, 0, 0);
            RequestDraw();
        }
    }

    public Performance GetPerformance()
    {
        lock (_gate)
            return new Performance(_active?.Frames ?? 0, _active?.Callbacks ?? 0, _presentations,
                _active?.CopyMilliseconds ?? 0, _drawMilliseconds);
    }

    public void Resize(double width, double height, float scale, double toolbarHeight)
    {
        if (width <= 0 || height <= 0 || scale <= 0)
        {
            return;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            uint pixelWidth = Math.Max(1, (uint)Math.Ceiling(width * scale));
            uint pixelHeight = Math.Max(1, (uint)Math.Ceiling(height * scale));
            bool scaleChanged = _scale != scale;
            _scale = scale;
            if (scaleChanged) RebuildRegionMasks();
            _toolbarHeight = (float)toolbarHeight;
            _blur.StandardDeviation = _blurRadius * scale;
            _swapChain.MatrixTransform = Matrix3x2.CreateScale(1 / scale);

            if (pixelWidth != _width || pixelHeight != _height)
            {
                _d2d.Target = null;
                _target?.Dispose();
                _target = null;
                _scene?.Dispose();
                _scene = null;
                _swapChain.ResizeBuffers(2, pixelWidth, pixelHeight, Format.Unknown, SwapChainFlags.None).CheckError();
                _width = pixelWidth;
                _height = pixelHeight;
                CreateTarget();
            }

            RequestDraw();
        }
    }

    private void UpdateFrame(FrameSource source, IntPtr sharedHandle, bool popup)
    {
        if (sharedHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Chromium entregó una textura compartida vacía.");
        }

        // CEF may reuse the shared texture as soon as this callback returns, so the GPU copy must finish before we do.
        // Only the command submission holds _gate; the wait happens outside it so UI-thread calls into the compositor
        // (resize, tab selection, hit-testing) are never blocked behind a slow GPU or driver.
        lock (_copyWaitGate)
        {
            long started = Stopwatch.GetTimestamp();
            ulong fenceValue;
            lock (_gate)
            {
                if (_disposed || source.Disposed)
                {
                    return;
                }

                using ID3D11Texture2D shared = _sharedDevice.OpenSharedResource1<ID3D11Texture2D>(sharedHandle);
                ref FrameTexture? frame = ref (popup ? ref source.Popup : ref source.Page);
                Texture2DDescription description = shared.Description;
                if (frame is null || !frame.Matches(description))
                {
                    frame?.Dispose();
                    frame = new FrameTexture(_device, _d2d, description);
                }

                _immediate.CopyResource(frame.Texture, shared);
                fenceValue = ++_fenceValue;
                _fenceContext.Signal(_copyFence, fenceValue);
                _copyFence.SetEventOnCompletion(fenceValue, _copyFinished.SafeWaitHandle.DangerousGetHandle());
                _immediate.Flush();
            }

            if (!_copyFinished.WaitOne(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("La GPU no terminó de copiar el fotograma de Chromium.");
            }

            lock (_gate)
            {
                if (_disposed || source.Disposed)
                {
                    return;
                }

                if (_copyFence.CompletedValue == ulong.MaxValue)
                {
                    throw new InvalidOperationException("Se perdió el dispositivo gráfico durante la copia del fotograma.");
                }
                if (_copyFence.CompletedValue < fenceValue)
                {
                    throw new InvalidOperationException("La copia del fotograma no se completó correctamente.");
                }
                source.Callbacks++;
                if (!popup) source.Frames++;
                source.CopyMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (_active == source) RequestDraw();
            }
        }
    }

    private void SetPopupBounds(FrameSource source, CefSharp.Structs.Rect bounds)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            source.PopupBounds = bounds;
            if (_active == source) RequestDraw();
        }
    }

    private void SetPopupVisible(FrameSource source, bool visible)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            source.PopupVisible = visible;
            if (!visible)
            {
                source.Popup?.Dispose();
                source.Popup = null;
            }

            if (_active == source) RequestDraw();
        }
    }

    private void CreateTarget()
    {
        using IDXGISurface surface = _swapChain.GetBuffer<IDXGISurface>(0);
        var properties = new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
            96,
            96,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        _target = _d2d.CreateBitmapFromDxgiSurface(surface, properties);
        var sceneProperties = new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
            96,
            96,
            BitmapOptions.Target);
        _scene = _d2d.CreateBitmap(new SizeI((int)_width, (int)_height), IntPtr.Zero, 0, sceneProperties);
        _d2d.Target = _target;
    }

    private void Draw()
    {
        if (_target is null || _scene is null)
        {
            return;
        }

        FrameTexture? page = _active?.Page;
        FrameTexture? popup = _active?.Popup;
        ID2D1Bitmap1? background = page?.Bitmap;
        float pageTop = _active?.PixelTop(page) ?? 0;
        if (page is not null && (pageTop > 0 || (_active is { PopupVisible: true } && popup is not null)))
        {
            _d2d.Target = _scene;
            _d2d.BeginDraw();
            _d2d.Clear(_dark ? new Color4(.14f, .14f, .14f, 1) : new Color4(.96f, .96f, .96f, 1));
            _d2d.Transform = Matrix3x2.CreateTranslation(0, pageTop);
            _d2d.DrawBitmap(page.Bitmap, 1, D2DInterpolationMode.Linear);
            if (_active is { PopupVisible: true } source && popup is not null)
            {
                var popupRect = new RawRectF(
                    source.PopupBounds.X * _scale, source.PopupBounds.Y * _scale,
                    (source.PopupBounds.X + source.PopupBounds.Width) * _scale,
                    (source.PopupBounds.Y + source.PopupBounds.Height) * _scale);
                _d2d.DrawBitmap(popup.Bitmap, popupRect, 1, D2DInterpolationMode.Linear, null, null);
            }
            _d2d.Transform = Matrix3x2.Identity;
            _d2d.EndDraw().CheckError();
            background = _scene;
        }

        _d2d.Target = _target;
        _d2d.BeginDraw();
        _d2d.Clear(_dark ? new Color4(.14f, .14f, .14f, 1) : new Color4(.96f, .96f, .96f, 1));
        if (background is not null)
        {
            _d2d.DrawBitmap(background, 1, D2DInterpolationMode.Linear);
            _blur.SetInput(0, background, true);
            DrawBlur(new RawRectF(0, 0, _width, Math.Min(_height, _toolbarHeight * _scale)));
        }

        float barHeight = Math.Min(_height, _toolbarHeight * _scale);
        _d2d.FillRectangle(new RawRectF(0, 0, _width, barHeight), _glassTint);
        float bookmarksTop = Math.Min(barHeight, 78 * _scale);
        _d2d.FillRectangle(new RawRectF(0, bookmarksTop, _width, barHeight), _bookmarkTint);
        for (int i = 0; i < _regions.Length; i++)
        {
            GlassRegion region = _regions[i];
            var rectangle = new RawRectF((float)region.X * _scale, (float)region.Y * _scale,
                (float)(region.X + region.Width) * _scale, (float)(region.Y + region.Height) * _scale);
            var layer = new LayerParameters1
            {
                ContentBounds = rectangle, GeometricMask = _regionMasks[i],
                MaskAntialiasMode = AntialiasMode.PerPrimitive, MaskTransform = Matrix3x2.Identity,
                Opacity = region.Visibility, LayerOptions = LayerOptions1.None
            };
            _d2d.PushLayer(layer, _menuLayer);
            if (background is not null) DrawBlur(rectangle);
            _menuTint.Color = _dark ? new Color4(.14f, .14f, .14f, region.Opacity) : new Color4(.98f, .98f, .98f, region.Opacity);
            _d2d.FillRectangle(rectangle, _menuTint);
            _d2d.FillRectangle(rectangle, _noiseBrush);
            _d2d.PopLayer();
        }

        _blur.SetInput(0, null, true);
        _d2d.EndDraw().CheckError();
        _swapChain.Present(0, PresentFlags.None).CheckError();
    }

    private void RebuildRegionMasks()
    {
        foreach (var mask in _regionMasks) mask.Dispose();
        _regionMasks.Clear();
        foreach (GlassRegion region in _regions)
            _regionMasks.Add(_factory.CreateRoundedRectangleGeometry(new RoundedRectangle(
                new System.Drawing.RectangleF((float)region.X * _scale, (float)region.Y * _scale,
                    (float)region.Width * _scale, (float)region.Height * _scale), 10 * _scale, 10 * _scale)));
    }

    private void DrawBlur(RawRectF rectangle)
    {
        if (_blurRadius <= 0) return;
        _d2d.PushAxisAlignedClip(rectangle, AntialiasMode.Aliased);
        _d2d.DrawImage(_blur);
        _d2d.PopAxisAlignedClip();
    }

    private void RequestDraw()
    {
        _dirty = true;
        _renderPending.Set();
    }

    private void RenderLoop()
    {
        while (!_stopping)
        {
            _renderPending.WaitOne();
            if (_stopping) break;
            try
            {
                lock (_gate)
                {
                    if (!_dirty || _disposed) continue;
                    _dirty = false;
                    long started = Stopwatch.GetTimestamp();
                    Draw();
                    _drawMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    _presentations++;
                }
            }
            catch (Exception exception)
            {
                RenderingFailed?.Invoke(exception);
                break;
            }
        }
    }

    internal sealed class FrameSource(GpuCompositor owner) : IDisposable
    {
        internal FrameTexture? Page;
        internal FrameTexture? Popup;
        internal CefSharp.Structs.Rect PopupBounds;
        internal bool PopupVisible;
        internal bool Disposed;
        internal long Frames;
        internal long Callbacks;
        internal double CopyMilliseconds;
        public void UpdateFrame(IntPtr handle, bool popup) => owner.UpdateFrame(this, handle, popup);
        public void SetPopupBounds(CefSharp.Structs.Rect bounds) => owner.SetPopupBounds(this, bounds);
        public void SetPopupVisible(bool visible) => owner.SetPopupVisible(this, visible);
        internal bool FullViewport = true;
        internal float TopInset;
        // Called for every pointer event on the UI thread: must not wait on the compositor lock, which the GPU threads hold
        // while drawing. Each field is read atomically; a value that is one frame stale during a resize is harmless.
        public double PresentedTopInset(float scale) => PixelTop(Volatile.Read(ref Page)) / scale;
        // Full viewports never resize while scrolling; the reveal is a GPU translation.
        // Short viewports derive placement from the received texture because a resize and its pixels arrive on different threads.
        internal float PixelTop(FrameTexture? page) => page is null ? 0
            : FullViewport ? MathF.Round(TopInset * owner._scale) : Math.Max(0, owner._height - page.PixelHeight);
        public void SetLayout(bool fullViewport, double topInset)
        {
            lock (owner._gate)
            {
                if (Disposed || (FullViewport == fullViewport && TopInset == (float)topInset)) return;
                FullViewport = fullViewport;
                TopInset = (float)topInset;
                if (owner._active == this && !owner._disposed) owner.RequestDraw();
            }
        }
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (Disposed) return;
                Disposed = true;
                Page?.Dispose();
                Popup?.Dispose();
                Page = Popup = null;
                owner._sources.Remove(this);
                if (owner._active == this)
                {
                    owner._active = null;
                    if (!owner._disposed) owner.RequestDraw();
                }
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _stopping = true;
        _renderPending.Set();
        _renderThread.Join();
        lock (_copyWaitGate) lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _panelNative.SetSwapChain(null).CheckError();
            _d2d.Target = null;
            foreach (FrameSource source in _sources.ToArray()) source.Dispose();
            _target?.Dispose();
            _scene?.Dispose();
            _bookmarkTint.Dispose();
            foreach (var mask in _regionMasks) mask.Dispose();
            _menuLayer.Dispose();
            _menuTint.Dispose();
            _noiseBrush.Dispose();
            _noiseBitmap.Dispose();
            _glassTint.Dispose();
            _blur.Dispose();
            _d2d.Dispose();
            _d2dDevice.Dispose();
            _factory.Dispose();
            _panelNative.Dispose();
            _swapChain.Dispose();
            _copyFinished.Dispose();
            _copyFence.Dispose();
            _fenceContext.Dispose();
            _immediate.Dispose();
            _sharedDevice.Dispose();
            _device.Dispose();
            _renderPending.Dispose();
        }
    }

    internal sealed class FrameTexture : IDisposable
    {
        private readonly Texture2DDescription _description;

        public FrameTexture(ID3D11Device device, ID2D1DeviceContext d2d, Texture2DDescription source)
        {
            _description = source;
            source.BindFlags = BindFlags.ShaderResource;
            source.CPUAccessFlags = CpuAccessFlags.None;
            source.MiscFlags = ResourceOptionFlags.None;
            source.Usage = ResourceUsage.Default;
            Texture = device.CreateTexture2D(source);
            using IDXGISurface surface = Texture.QueryInterface<IDXGISurface>();
            var properties = new BitmapProperties1(
                new D2DPixelFormat(source.Format, D2DAlphaMode.Premultiplied));
            Bitmap = d2d.CreateBitmapFromDxgiSurface(surface, properties);
        }

        public ID3D11Texture2D Texture { get; }
        public ID2D1Bitmap1 Bitmap { get; }
        public uint PixelHeight => _description.Height;

        public bool Matches(Texture2DDescription description) =>
            _description.Width == description.Width &&
            _description.Height == description.Height &&
            _description.Format == description.Format;

        public void Dispose()
        {
            Bitmap.Dispose();
            Texture.Dispose();
        }
    }
}
