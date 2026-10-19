using Shotwise.Core.Geometry;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Shotwise.Recording;

/// <summary>BGRA to NV12 conversion without a CPU readback. Owned by the MF/D3D worker.</summary>
internal sealed class GpuVideoConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11VideoDevice _video;
    private readonly ID3D11VideoContext _context;
    private readonly ID3D11DeviceContext _immediate;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly SizeI _size;
    public GpuVideoConverter(ID3D11Device device, SizeI size, int fps)
    {
        _device = device; _size = size;
        try
        {
            _video = device.QueryInterface<ID3D11VideoDevice>();
            _immediate = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
            _context = _immediate.QueryInterface<ID3D11VideoContext>();
            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputWidth = (uint)size.Width, InputHeight = (uint)size.Height,
                OutputWidth = (uint)size.Width, OutputHeight = (uint)size.Height,
                InputFrameRate = new Rational((uint)fps, 1), OutputFrameRate = new Rational((uint)fps, 1),
                Usage = VideoUsage.PlaybackNormal
            };
            _video.CreateVideoProcessorEnumerator(content, out _enumerator).CheckError();
            _video.CreateVideoProcessor(_enumerator, 0, out _processor).CheckError();
            _context.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
            _context.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        }
        catch { Dispose(); throw; }
    }
    public ID3D11Texture2D Convert(ID3D11Texture2D input)
    {
        // The encoder can open the VP result on its own D3D device. RenderTarget alone
        // permits the blit but does not make the surface shareable with that device.
        var output = _device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)_size.Width, (uint)_size.Height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, miscFlags: ResourceOptionFlags.Shared));
        try
        {
            _video.CreateVideoProcessorInputView(input, _enumerator, new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D }, out var inputView).CheckError();
            using (inputView)
            {
                _video.CreateVideoProcessorOutputView(output, _enumerator, new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D }, out var outputView).CheckError();
                using (outputView)
                {
                    var stream = new VideoProcessorStream { Enable = true, InputSurface = inputView };
                    _context.VideoProcessorBlt(_processor, outputView, 0, 1, new[] { stream }).CheckError();
                    // Submit producer commands before handing the shared surface to MF's device.
                    _immediate.Flush();
                }
            }
            return output;
        }
        catch { output.Dispose(); throw; }
    }
    public void Dispose() { _processor?.Dispose(); _enumerator?.Dispose(); _context?.Dispose(); _immediate?.Dispose(); _video?.Dispose(); }
}
