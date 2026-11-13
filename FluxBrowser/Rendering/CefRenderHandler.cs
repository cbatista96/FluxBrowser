using System.Runtime.InteropServices;
using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.Structs;
using Range = CefSharp.Structs.Range;

namespace FluxBrowser.Rendering;

internal sealed class CefRenderHandler : IRenderHandler
{
    private readonly object _geometryGate = new();
    private readonly GpuCompositor.FrameSource _compositor;
    private readonly IntPtr _window;
    private Rect _view = new(0, 0, 1, 1);
    private float _scale = 1;
    private int _failed;
    private int _firstFrame;
    private int _offsetX;
    private int _offsetY;

    public CefRenderHandler(GpuCompositor.FrameSource compositor, IntPtr window)
    {
        _compositor = compositor;
        _window = window;
    }

    public event Action? FirstFrame;
    public event Action<Exception>? RenderingFailed;
    public event Action<CursorType>? CursorChanged;

    public bool SetGeometry(double width, double height, float scale, double offsetX, double offsetY)
    {
        lock (_geometryGate)
        {
            var view = new Rect(0, 0, Math.Max(1, (int)Math.Ceiling(width)), Math.Max(1, (int)Math.Ceiling(height)));
            bool changed = _view.Width != view.Width || _view.Height != view.Height || _scale != scale;
            _view = view;
            _scale = scale;
            _offsetX = (int)Math.Round(offsetX * scale);
            _offsetY = (int)Math.Round(offsetY * scale);
            return changed;
        }
    }

    public ScreenInfo? GetScreenInfo()
    {
        lock (_geometryGate)
        {
            return new ScreenInfo { DeviceScaleFactor = _scale };
        }
    }

    public Rect GetViewRect()
    {
        lock (_geometryGate)
        {
            return _view;
        }
    }

    public bool GetScreenPoint(int viewX, int viewY, out int screenX, out int screenY)
    {
        NativePoint point;
        lock (_geometryGate)
        {
            point = new NativePoint
            {
                X = (int)Math.Round(viewX * _scale) + _offsetX,
                Y = (int)Math.Round(viewY * _scale) + _offsetY
            };
        }

        bool success = ClientToScreen(_window, ref point);
        screenX = point.X;
        screenY = point.Y;
        return success;
    }

    public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo info)
    {
        if (Volatile.Read(ref _failed) != 0)
        {
            return;
        }

        try
        {
            _compositor.UpdateFrame(info.SharedTextureHandle, type == PaintElementType.Popup);
            if (type == PaintElementType.View && Interlocked.Exchange(ref _firstFrame, 1) == 0)
            {
                FirstFrame?.Invoke();
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    public void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height) =>
        Fail(new InvalidOperationException("Chromium no proporcionó la textura GPU compartida requerida."));

    public void OnCursorChange(IntPtr cursor, CursorType type, CursorInfo customCursorInfo) =>
        CursorChanged?.Invoke(type);

    public void OnPopupShow(bool show) => RunRenderingAction(() => _compositor.SetPopupVisible(show));

    public void OnPopupSize(Rect rect) => RunRenderingAction(() => _compositor.SetPopupBounds(rect));

    public bool StartDragging(IDragData dragData, DragOperationsMask mask, int x, int y) => false;

    public void UpdateDragCursor(DragOperationsMask operation) { }

    public void OnImeCompositionRangeChanged(Range selectedRange, Rect[] characterBounds) { }

    public void OnVirtualKeyboardRequested(IBrowser browser, TextInputMode inputMode) { }

    public void Dispose() { }

    private void RunRenderingAction(Action action)
    {
        if (Volatile.Read(ref _failed) != 0)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void Fail(Exception exception)
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0)
        {
            RenderingFailed?.Invoke(exception);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
}
