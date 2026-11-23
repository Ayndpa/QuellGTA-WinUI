using System.Drawing;
using static QuellGTA.Native.OverlayNative;

namespace QuellGTA.Services;

/// <summary>一个鼠标穿透、不抢焦点的置顶分层窗口，内容由 32 位带透明度的位图直接提供。</summary>
internal sealed class LayeredWindow : IDisposable
{
    private readonly nint _hwnd;
    private bool _visible;
    private bool? _captureHidden;
    private int _x = int.MinValue, _y = int.MinValue, _width, _height;

    public LayeredWindow()
    {
        _hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "Static", null, WS_POPUP, 0, 0, 1, 1, 0, 0, 0, 0);
    }

    public void SetCaptureHidden(bool hidden)
    {
        if (_captureHidden == hidden) return;
        _captureHidden = hidden;
        SetWindowDisplayAffinity(_hwnd, hidden ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    public void Render(Bitmap bitmap, int x, int y)
    {
        var screenDc = GetDC(0);
        var memoryDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memoryDc, hBitmap);
        try
        {
            var destination = new POINT { X = x, Y = y };
            var size = new SIZE { Cx = bitmap.Width, Cy = bitmap.Height };
            var source = new POINT();
            var blend = new BLENDFUNCTION { Op = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(_hwnd, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA);
            (_x, _y, _width, _height) = (x, y, bitmap.Width, bitmap.Height);
        }
        finally
        {
            SelectObject(memoryDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memoryDc);
            ReleaseDC(0, screenDc);
        }
        if (!_visible)
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            _visible = true;
        }
        // 其他置顶窗口可能盖上来，重新声明一次置顶
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void MoveTo(int x, int y)
    {
        if (!_visible || (x == _x && y == _y)) return;
        (_x, _y) = (x, y);
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void MoveCentered(int centerX, int top) => MoveTo(centerX - _width / 2, top);

    public void Hide()
    {
        if (!_visible) return;
        ShowWindow(_hwnd, SW_HIDE);
        _visible = false;
    }

    public void Dispose()
    {
        if (_hwnd != 0) DestroyWindow(_hwnd);
    }
}
