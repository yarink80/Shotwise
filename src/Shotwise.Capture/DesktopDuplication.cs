using System.Runtime.InteropServices;
using Shotwise.Core.Capture;
using Shotwise.Core.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Shotwise.Capture;

internal static class DesktopDuplication
{
    internal static BgraImage Capture(MonitorInfo monitor, CancellationToken ct)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success; ai++)
        using (adapter)
        for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success; oi++)
        using (output)
        {
            if (output.Description.Monitor != monitor.HMonitor) continue;
            if (output.Description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified)) throw new InvalidOperationException("Rotated outputs use the orientation-aware GDI fallback.");
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
            using (device) using (context)
            // IDXGIOutput1.DuplicateOutput (BGRA desktop format). The Vortice 3.8 IDXGIOutput5.DuplicateOutput1 wrapper crashed
            // with an AccessViolationException in the integrated app (P3); HDR snapshots use the WGC FP16 path instead.
            using (var output1 = output.QueryInterface<IDXGIOutput1>())
            using (var duplicate = output1.DuplicateOutput(device))
            {
                ct.ThrowIfCancellationRequested();
                duplicate.AcquireNextFrame(150, out _, out var resource).CheckError();
                try
                {
                    using (resource)
                    using (var texture = resource.QueryInterface<ID3D11Texture2D>())
                    {
                        var desc = texture.Description;
                        desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None; desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
                        using var staging = device.CreateTexture2D(desc);
                        context.CopyResource(staging, texture);
                        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            var image = new BgraImage((int)desc.Width, (int)desc.Height);
                            for (int y = 0; y < image.Height; y++) Marshal.Copy(mapped.DataPointer + checked(y * (int)mapped.RowPitch), image.Pixels, y * image.Stride, image.Stride);
                            for (int i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 255;
                            // Rotated duplication surfaces are not in desktop orientation; let GDI handle them.
                            if (image.Width != monitor.BoundsPx.Width || image.Height != monitor.BoundsPx.Height) throw new InvalidOperationException("Rotated duplication output.");
                            return image;
                        }
                        finally { context.Unmap(staging, 0); }
                    }
                }
                finally { duplicate.ReleaseFrame(); }
            }
        }
        throw new InvalidOperationException("No DXGI output for monitor.");
    }
}
