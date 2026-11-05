using System.Numerics;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace FloatPrototype.Float;

/// <summary>
/// 非分层窗口的显示层：D3D11 交换链经 DirectComposition 合成到 HWND。
/// 窗口不参与 DWM 重定向，合成层负责带 Alpha 的显示，鼠标命中仍由窗口处理。
/// </summary>
internal sealed class CompositionPresenter : IDisposable
{
    private readonly IntPtr _hwnd;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private IDCompositionDevice? _dcomp;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _visual;
    private byte[] _pixels = Array.Empty<byte>();
    private int _width;
    private int _height;
    private double _dpiScale = 1.0;

    public CompositionPresenter(IntPtr hwnd) => _hwnd = hwnd;

    public void Initialize(double dpiScale)
    {
        _dpiScale = dpiScale;
        _device = D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 });
        _context = _device.ImmediateContext;
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _dcomp.CreateTargetForHwnd(_hwnd, true, out _target);
        _dcomp.CreateVisual(out _visual);
        _target.SetRoot(_visual);
    }

    public void EnsureSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_swapChain is null)
        {
            using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var desc = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,
                Flags = SwapChainFlags.None,
            };
            _swapChain = factory.CreateSwapChainForComposition(_device, desc);
            _visual!.SetContent(_swapChain);
            _width = width;
            _height = height;
            ApplyScale();
            return;
        }

        if (width == _width && height == _height) return;
        _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        _width = width;
        _height = height;
        ApplyScale();
    }

    public void SetDpiScale(double dpiScale)
    {
        _dpiScale = dpiScale;
        ApplyScale();
    }

    private void ApplyScale()
    {
        // DirectComposition 使用 DIP 坐标；把像素缓冲映射为 1:1 像素。
        _visual?.SetTransform(Matrix3x2.CreateScale((float)(1.0 / Math.Max(0.5, _dpiScale))));
    }

    public void Present(BitmapSource bitmap)
    {
        if (_swapChain is null || _context is null) return;

        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int stride = width * 4;
        int required = stride * height;
        if (_pixels.Length < required) _pixels = new byte[required];
        bitmap.CopyPixels(_pixels, stride, 0);

        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _context.UpdateSubresource(_pixels, backBuffer, 0, (uint)stride, (uint)required, null);
        _swapChain.Present(1, PresentFlags.None);
        _dcomp?.Commit();
    }

    public void Dispose()
    {
        _visual?.Dispose();
        _target?.Dispose();
        _dcomp?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
