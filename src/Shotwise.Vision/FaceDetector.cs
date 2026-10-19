using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Shotwise.Core.Geometry;
using Shotwise.Core.Imaging;
using Shotwise.Core.Vision;

namespace Shotwise.Vision;

/// <summary>
/// YuNet (OpenCV <c>FaceDetectorYN</c>) face detector. The ONNX model comes from <see cref="IModelManager"/>; when it has not
/// been downloaded the detector logs once and returns no faces (the app stays fully functional without it).
/// </summary>
public sealed class FaceDetector : IFaceDetector, IDisposable
{
    private const int MaxSide = 2000;
    private readonly IModelManager _models;
    private readonly ILogger<FaceDetector> _log;
    private readonly object _lock = new();
    private FaceDetectorYN? _det;
    private Size _detSize;
    private bool _warned;

    public FaceDetector(IModelManager models, ILogger<FaceDetector> log)
    {
        _models = models;
        _log = log;
    }

    public async Task<IReadOnlyList<Rect2>> DetectAsync(BgraImage img, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(img);
        if (!_models.IsAvailable(ModelIds.YuNet))
        {
            if (!_warned) { _warned = true; _log.LogWarning("YuNet face model is not installed; face detection returns no results"); }
            return Array.Empty<Rect2>();
        }
        // available => EnsureAsync resolves the verified local path without any download
        var path = await _models.EnsureAsync(ModelIds.YuNet, null, ct).ConfigureAwait(false);
        return await Task.Run(() => Detect(img, path, ct), ct).ConfigureAwait(false);
    }

    private IReadOnlyList<Rect2> Detect(BgraImage img, string modelPath, CancellationToken ct)
    {
        using var bgr = CvInterop.ToBgr(img);
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(bgr.Cols, bgr.Rows));
        using var work = new Mat();
        if (scale < 1) Cv2.Resize(bgr, work, new Size((int)(bgr.Cols * scale), (int)(bgr.Rows * scale)), 0, 0, InterpolationFlags.Area);
        else bgr.CopyTo(work);

        lock (_lock)
        {
            ct.ThrowIfCancellationRequested();
            var size = new Size(work.Cols, work.Rows);
            if (_det is null || _detSize != size)
            {
                _det?.Dispose();                       // this binding has no SetInputSize: recreate when the frame size changes
                _det = FaceDetectorYN.Create(modelPath, "", size, 0.7f, 0.3f, 5000);
                _detSize = size;
            }
            using var faces = new Mat();
            _det.Detect(work, faces);
            var list = new List<Rect2>();
            if (faces.Rows > 0 && faces.Cols >= 15)
            {
                for (var i = 0; i < faces.Rows; i++)
                {
                    double x = faces.At<float>(i, 0), y = faces.At<float>(i, 1), w = faces.At<float>(i, 2), h = faces.At<float>(i, 3);
                    var r = new Rect2(x / scale, y / scale, w / scale, h / scale).Intersect(new Rect2(0, 0, img.Width, img.Height));
                    if (!r.IsEmpty) list.Add(r);
                }
            }
            return list;
        }
    }

    public void Dispose()
    {
        lock (_lock) { _det?.Dispose(); _det = null; }
    }
}
