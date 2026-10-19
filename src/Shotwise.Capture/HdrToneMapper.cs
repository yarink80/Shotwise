using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DCommon;
using Vortice.DXGI;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace Shotwise.Capture;

/// <summary>
/// FP16 scRGB (linear, 1.0 = 80 nits) monitor frame → 8-bit sRGB BGRA texture:
/// HdrToneMap (HDR display mode; highlights above SDR white rolled off) → WhiteLevelAdjustment (SDR white → 1.0) →
/// ColorManagement scRGB → sRGB. The last step applies the sRGB transfer function (R1-#6); without it linear values were stored as
/// sRGB code values (linear 0.2 → 51 instead of ≈124), which made HDR captures dark and crushed.
/// </summary>
internal static class HdrToneMapper
{
    // D2D1_COLORMANAGEMENT_PROP_*
    private const uint SourceColorContext = 0, DestinationColorContext = 2, Quality = 5;
    // D2D1_COLORMANAGEMENT_QUALITY_NORMAL / _BEST
    private const uint QualityNormal = 1, QualityBest = 2;
    // D2D1_HDRTONEMAP_PROP_DISPLAY_MODE / D2D1_HDRTONEMAP_DISPLAY_MODE_HDR
    private const uint DisplayModeProperty = 2, DisplayModeHdr = 1;

    internal static unsafe ID3D11Texture2D Map(ID3D11Device device, ID3D11Texture2D source, double whiteLevel)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var d2d = D2D1.D2D1CreateDevice(dxgi);
        using var context = d2d.CreateDeviceContext(DeviceContextOptions.None);
        using var inputSurface = source.QueryInterface<IDXGISurface>();
        using var input = context.CreateBitmapFromDxgiSurface(inputSurface, new BitmapProperties1(new PixelFormat(Format.R16G16B16A16_Float, AlphaMode.Ignore), 96, 96, BitmapOptions.None));
        var desc = source.Description;
        desc.Format = Format.B8G8R8A8_UNorm; desc.BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource; desc.CPUAccessFlags = CpuAccessFlags.None; desc.Usage = ResourceUsage.Default; desc.MiscFlags = ResourceOptionFlags.None;
        var result = device.CreateTexture2D(desc);
        try
        {
            using var targetSurface = result.QueryInterface<IDXGISurface>();
            using var target = context.CreateBitmapFromDxgiSurface(targetSurface, new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Ignore), 96, 96, BitmapOptions.Target));
            using var tone = new ID2D1Effect(context.CreateEffect(EffectGuids.HdrToneMap));
            using var white = new ID2D1Effect(context.CreateEffect(EffectGuids.WhiteLevelAdjustment));
            using var encode = new ID2D1Effect(context.CreateEffect(EffectGuids.ColorManagement));
            using var scRgb = context.CreateColorContext(ColorSpace.ScRgb, null!, 0);
            using var sRgb = context.CreateColorContext(ColorSpace.Srgb, null!, 0);
            // Measured on this GPU/D2D (FX-2 harness, FP16 in/out):
            //  * WhiteLevelAdjustment multiplies by INPUT/OUTPUT white level, so (input 80, output SDR white) scales SDR white (e.g.
            //    240 nits = 3.0 scRGB) down to 1.0. The previous (SDR white, 80) order multiplied by 3 and clipped all SDR content.
            //  * HdrToneMap in its default SDR display mode lifts mid-tones (0.2 → 0.27); HDR display mode keeps content below the
            //    output peak unchanged (0.2 → 0.2) and rolls off only near the peak. Its knee sits at about half the output peak, so
            //    the peak is set to 2 × SDR white: SDR content (UI, text, photos up to SDR white) is reproduced exactly (SDR white →
            //    255) and HDR highlights are compressed into 1–2 × SDR white, then clip at white like the system's own SDR screenshots.
            float sdrWhite = (float)Math.Max(80, whiteLevel);
            float inputMax = 1000, outputMax = Math.Min(inputMax, 2 * sdrWhite), inputWhite = 80, outputWhite = sdrWhite;
            uint hdrDisplayMode = DisplayModeHdr;
            tone.SetValue(0, PropertyType.Float, &inputMax, sizeof(float));
            tone.SetValue(1, PropertyType.Float, &outputMax, sizeof(float));
            tone.SetValue(DisplayModeProperty, PropertyType.Enum, &hdrDisplayMode, sizeof(uint));
            white.SetValue(0, PropertyType.Float, &inputWhite, sizeof(float));
            white.SetValue(1, PropertyType.Float, &outputWhite, sizeof(float));
            IntPtr src = scRgb.NativePointer, dst = sRgb.NativePointer;
            encode.SetValue(SourceColorContext, PropertyType.ColorContext, &src, (uint)IntPtr.Size);
            encode.SetValue(DestinationColorContext, PropertyType.ColorContext, &dst, (uint)IntPtr.Size);
            // Best (float) quality needs FL10+ with float render targets; Normal is the documented fallback.
            uint quality = QualityBest;
            try { encode.SetValue(Quality, PropertyType.Enum, &quality, sizeof(uint)); }
            catch (SharpGen.Runtime.SharpGenException) { quality = QualityNormal; encode.SetValue(Quality, PropertyType.Enum, &quality, sizeof(uint)); }
            tone.SetInput(0, input, true); white.SetInputEffect(0, tone); encode.SetInputEffect(0, white);
            context.Target = target;
            context.BeginDraw(); using (var image = encode.Output) context.DrawImage(image); context.EndDraw().CheckError();
            context.Target = null;
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
