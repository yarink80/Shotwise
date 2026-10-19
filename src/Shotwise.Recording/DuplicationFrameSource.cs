using System.Runtime.InteropServices;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Shotwise.Recording;

internal sealed class DuplicationFrameSource : IDisposable
{
    private readonly List<Output> _outputs = new();
    private readonly RectI _bounds;
    private sealed class Output : IDisposable
    {
        public required ID3D11Device Device;
        public required ID3D11DeviceContext Context;
        public required IDXGIOutputDuplication Duplication;
        public required RectI Bounds;
        public BgraImage? Last;
        public ID3D11Texture2D? Staging;
        public void Dispose() { Staging?.Dispose(); Duplication.Dispose(); Context.Dispose(); Device.Dispose(); }
    }
    public DuplicationFrameSource(RectI bounds)
    {
        _bounds = bounds;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                using (output)
                {
                    var rect = output.Description.DesktopCoordinates;
                    var area = new RectI(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                    if (area.X >= bounds.X + bounds.Width || area.X + area.Width <= bounds.X || area.Y >= bounds.Y + bounds.Height || area.Y + area.Height <= bounds.Y) continue;
                    if (output.Description.Rotation != ModeRotation.Identity && output.Description.Rotation != ModeRotation.Unspecified) throw new NotSupportedException("Rotated output uses GDI fallback.");
                    D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
                    try
                    {
                        using (var multi = context.QueryInterface<ID3D11Multithread>()) multi.SetMultithreadProtected(true);
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        _outputs.Add(new Output { Device = device, Context = context, Duplication = output1.DuplicateOutput(device), Bounds = area });
                    }
                    catch { context.Dispose(); device.Dispose(); throw; }
                }
            }
            if (_outputs.Count == 0) throw new InvalidOperationException("No desktop output for target.");
        }
        catch { Dispose(); throw; }
    }
    public BgraImage? Capture()
    {
        var result = new BgraImage(_bounds.Width, _bounds.Height);
        foreach (var output in _outputs)
        {
            var status = output.Duplication.AcquireNextFrame(0, out var info, out var resource);
            if (status.Code != unchecked((int)0x887A0027))
            {
                status.CheckError();
                try
                {
                    using (resource)
                    {
                        if (info.TotalMetadataBufferSize > 0 || output.Last == null)
                        {
                            using var texture = resource.QueryInterface<ID3D11Texture2D>();
                            var desc = texture.Description; desc.BindFlags = BindFlags.None; desc.Usage = ResourceUsage.Staging; desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
                            output.Staging ??= output.Device.CreateTexture2D(desc);
                            var rectangles = new List<Vortice.RawRect>();
                            if (output.Last == null) rectangles.Add(new Vortice.RawRect(0, 0, (int)desc.Width, (int)desc.Height));
                            else
                            {
                                var dirty = new Vortice.RawRect[info.TotalMetadataBufferSize / 16 + 1];
                                output.Duplication.GetFrameDirtyRects((uint)(dirty.Length * 16), dirty, out var dirtyBytes).CheckError();
                                rectangles.AddRange(dirty.Take((int)dirtyBytes / 16));
                                var moveSize = Marshal.SizeOf<OutduplMoveRect>();
                                var moves = new OutduplMoveRect[info.TotalMetadataBufferSize / moveSize + 1];
                                output.Duplication.GetFrameMoveRects((uint)(moves.Length * moveSize), moves, out var moveBytes).CheckError();
                                rectangles.AddRange(moves.Take((int)moveBytes / moveSize).Select(m => m.DestinationRect));
                            }
                            foreach (var dirty in rectangles)
                                output.Context.CopySubresourceRegion(output.Staging, 0, (uint)dirty.Left, (uint)dirty.Top, 0, texture, 0, new Vortice.Mathematics.Box(dirty.Left, dirty.Top, 0, dirty.Right, dirty.Bottom, 1));
                            output.Context.Map(output.Staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
                            try
                            {
                                output.Last ??= new BgraImage((int)desc.Width, (int)desc.Height);
                                foreach (var dirty in rectangles)
                                    for (var y = dirty.Top; y < dirty.Bottom; y++) Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch + dirty.Left * 4, output.Last.Pixels, (y * output.Last.Width + dirty.Left) * 4, (dirty.Right - dirty.Left) * 4);
                            }
                            finally { output.Context.Unmap(output.Staging, 0); }
                        }
                    }
                }
                finally { output.Duplication.ReleaseFrame(); }
            }
            if (output.Last == null) return null;
            var left = Math.Max(_bounds.X, output.Bounds.X); var top = Math.Max(_bounds.Y, output.Bounds.Y);
            var width = Math.Min(_bounds.X + _bounds.Width, output.Bounds.X + output.Bounds.Width) - left;
            var height = Math.Min(_bounds.Y + _bounds.Height, output.Bounds.Y + output.Bounds.Height) - top;
            for (var row = 0; row < height; row++) Buffer.BlockCopy(output.Last.Pixels, ((top - output.Bounds.Y + row) * output.Last.Width + left - output.Bounds.X) * 4, result.Pixels, ((top - _bounds.Y + row) * result.Width + left - _bounds.X) * 4, width * 4);
        }
        return result;
    }
    public void Dispose() { foreach (var output in _outputs) output.Dispose(); _outputs.Clear(); }
}
