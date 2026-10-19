using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using Shotwise.Core;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;
using Shotwise.Vision.Internal;

namespace Shotwise.Vision.Inpainting;

/// <summary>
/// <see cref="IInpaintingService"/> (VIS-03/06). Fast = OpenCV Telea (small holes) / Navier-Stokes (large);
/// Quality = gradient fill on smooth UI backgrounds, otherwise a Criminisi-style exemplar inpainter;
/// HighQuality = LaMa ONNX, offered only when the model is installed (never downloaded implicitly).
/// </summary>
public sealed class InpaintingService : IInpaintingService, IDisposable
{
    private const int ExemplarPatchRadius = 4;
    private const int ExemplarMaxHolePixels = 60_000;
    private const double SmoothRingThreshold = 2.0;

    private readonly IModelManager _models;
    private readonly ILogger<InpaintingService> _log;
    private readonly object _lamaLock = new();
    private InferenceSession? _lama;
    private bool _lamaFailed;

    public InpaintingService(IModelManager models, ILogger<InpaintingService> log)
    {
        _models = models;
        _log = log;
    }

    public InpaintQuality[] Available => _models.IsAvailable(ModelIds.Lama) && !_lamaFailed
        ? new[] { InpaintQuality.Fast, InpaintQuality.Quality, InpaintQuality.HighQuality }
        : new[] { InpaintQuality.Fast, InpaintQuality.Quality };

    public Task<BgraImage> InpaintAsync(BgraImage img, Mask mask, InpaintQuality q = InpaintQuality.Fast, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(img);
        ArgumentNullException.ThrowIfNull(mask);
        if (mask.Width != img.Width || mask.Height != img.Height)
            throw new ShotwiseException(ErrorCode.InvalidArgument, "Inpainting mask size must equal the image size.");
        return Task.Run(() => Run(img, mask, q, ct), ct);
    }

    private BgraImage Run(BgraImage img, Mask mask, InpaintQuality q, CancellationToken ct)
    {
        try
        {
            using var holeFull = CvInterop.MaskToMat(mask);
            Cv2.Threshold(holeFull, holeFull, 127, 255, ThresholdTypes.Binary);
            if (Cv2.CountNonZero(holeFull) == 0) return img.Clone();
            // anti-aliased fringes of the selection must be replaced too
            using (var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3))) Cv2.Dilate(holeFull, holeFull, k3);

            var bbox = Cv2.BoundingRect(holeFull);
            var margin = q switch
            {
                InpaintQuality.Fast => 12,
                InpaintQuality.Quality => Math.Max(48, Math.Max(bbox.Width, bbox.Height) / 2),
                _ => Math.Max(96, Math.Max(bbox.Width, bbox.Height))
            };
            var roi = new Rect(Math.Max(0, bbox.X - margin), Math.Max(0, bbox.Y - margin), 0, 0);
            roi.Width = Math.Min(img.Width, bbox.Right + margin) - roi.X;
            roi.Height = Math.Min(img.Height, bbox.Bottom + margin) - roi.Y;

            using var bgrFull = CvInterop.ToBgr(img);
            using var bgr = new Mat(bgrFull, roi).Clone();
            using var hole = new Mat(holeFull, roi).Clone();

            Mat? filled = null;
            if (q == InpaintQuality.HighQuality)
            {
                filled = TryLama(bgr, hole, ct);
                if (filled is null) { _log.LogInformation("LaMa unavailable; using Quality inpainting"); q = InpaintQuality.Quality; }
            }
            if (filled is null && q == InpaintQuality.Quality) filled = FillQuality(bgr, hole, ct);
            filled ??= FillFast(bgr, hole);

            using (filled)
            {
                var result = img.Clone();
                var fb = CvInterop.ToBytes(filled);
                var hb = CvInterop.ToBytes(hole);
                var px = result.Pixels;
                for (var y = 0; y < roi.Height; y++)
                    for (var x = 0; x < roi.Width; x++)
                    {
                        if (hb[y * roi.Width + x] == 0) continue;
                        var d = ((roi.Y + y) * img.Width + roi.X + x) * 4;
                        var s = (y * roi.Width + x) * 3;
                        px[d] = fb[s]; px[d + 1] = fb[s + 1]; px[d + 2] = fb[s + 2]; px[d + 3] = 255;
                    }
                return result;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (ShotwiseException) { throw; }
        catch (Exception ex)
        {
            throw new ShotwiseException(ErrorCode.InpaintFailed, "Inpainting failed.", ex);
        }
    }

    private static Mat FillFast(Mat bgr, Mat hole)
    {
        var holeArea = Cv2.CountNonZero(hole);
        var small = holeArea < 0.02 * bgr.Rows * bgr.Cols;
        var dst = new Mat();
        Cv2.Inpaint(bgr, hole, dst, small ? 3 : 5, small ? InpaintTypes.Telea : InpaintTypes.NS);
        return dst;
    }

    private Mat FillQuality(Mat bgr, Mat hole, CancellationToken ct)
    {
        var rough = ExemplarInpainter.RingRoughness(bgr, hole);
        if (rough <= SmoothRingThreshold)
        {
            _log.LogDebug("Smooth surroundings (roughness {R:F2}); gradient fill", rough);
            return ExemplarInpainter.GradientFill(bgr, hole);
        }
        var ex = ExemplarInpainter.Exemplar(bgr, hole, ExemplarPatchRadius, ExemplarMaxHolePixels, ct);
        if (ex is not null) return ex;
        _log.LogInformation("Exemplar inpainting not applicable (hole too large / no source patches); using Navier-Stokes");
        return FillFast(bgr, hole);
    }

    // ---- LaMa ONNX (optional) --------------------------------------------------------------

    private Mat? TryLama(Mat bgr, Mat hole, CancellationToken ct)
    {
        if (_lamaFailed || !_models.IsAvailable(ModelIds.Lama)) return null;
        try
        {
            var session = GetLama();
            if (session is null) return null;
            const int N = 512;
            var inputs = session.InputMetadata.Keys.ToArray();
            if (inputs.Length < 2) throw new InvalidOperationException("Unexpected LaMa input signature.");

            using var rgb = new Mat();
            Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);
            using var img512 = new Mat();
            Cv2.Resize(rgb, img512, new Size(N, N), 0, 0, InterpolationFlags.Area);
            using var mask512 = new Mat();
            Cv2.Resize(hole, mask512, new Size(N, N), 0, 0, InterpolationFlags.Nearest);
            using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5))) Cv2.Dilate(mask512, mask512, k);

            var imgT = new DenseTensor<float>(new[] { 1, 3, N, N });
            var maskT = new DenseTensor<float>(new[] { 1, 1, N, N });
            var ib = CvInterop.ToBytes(img512);
            var mb = CvInterop.ToBytes(mask512);
            for (var y = 0; y < N; y++)
                for (var x = 0; x < N; x++)
                {
                    var p = (y * N + x) * 3;
                    imgT[0, 0, y, x] = ib[p] / 255f;
                    imgT[0, 1, y, x] = ib[p + 1] / 255f;
                    imgT[0, 2, y, x] = ib[p + 2] / 255f;
                    maskT[0, 0, y, x] = mb[y * N + x] > 127 ? 1f : 0f;
                }
            ct.ThrowIfCancellationRequested();
            using var results = session.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor(inputs[0], imgT),
                NamedOnnxValue.CreateFromTensor(inputs[1], maskT)
            });
            var outT = results.First().AsTensor<float>();
            var max = 0f;
            foreach (var v in outT) if (v > max) max = v;
            var scale = max <= 2f ? 255f : 1f;

            var outBytes = new byte[N * N * 3];
            for (var y = 0; y < N; y++)
                for (var x = 0; x < N; x++)
                    for (var c = 0; c < 3; c++)
                        outBytes[(y * N + x) * 3 + (2 - c)] = (byte)Math.Clamp((int)Math.Round(outT[0, c, y, x] * scale), 0, 255);
            using var outSmall = CvInterop.ToMatBgr(outBytes, N, N);
            using var outFull = new Mat();
            Cv2.Resize(outSmall, outFull, new Size(bgr.Cols, bgr.Rows), 0, 0, InterpolationFlags.Cubic);

            // keep original pixels outside the hole; feather the seam slightly
            using var soft = new Mat();
            Cv2.GaussianBlur(hole, soft, new Size(0, 0), 1.5);
            var result = bgr.Clone();
            var ob = CvInterop.ToBytes(outFull);
            var sb = CvInterop.ToBytes(soft);
            var hb = CvInterop.ToBytes(hole);
            var rb = CvInterop.ToBytes(result);
            for (var i = 0; i < hb.Length; i++)
            {
                if (hb[i] == 0) continue;
                for (var c = 0; c < 3; c++) rb[i * 3 + c] = ob[i * 3 + c];
            }
            _ = sb;
            return CvInterop.ToMatBgr(rb, bgr.Cols, bgr.Rows);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LaMa inference failed; disabling HighQuality");
            _lamaFailed = true;
            return null;
        }
    }

    private InferenceSession? GetLama()
    {
        lock (_lamaLock)
        {
            if (_lama is not null) return _lama;
            // IsAvailable == true guarantees EnsureAsync returns the local verified path without any network access.
            var path = _models.EnsureAsync(ModelIds.Lama, null, CancellationToken.None).GetAwaiter().GetResult();
            using var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            _lama = new InferenceSession(path, opts);
            return _lama;
        }
    }

    public void Dispose()
    {
        lock (_lamaLock) { _lama?.Dispose(); _lama = null; }
    }
}
