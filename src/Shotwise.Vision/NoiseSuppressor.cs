using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Shotwise.Core.Media;
using Shotwise.Core.Vision;

namespace Shotwise.Vision;

/// <summary>
/// Streaming noise-suppression host (VIS-08). Runs a user-provided ONNX model (id <see cref="ModelIds.NoiseSuppressor"/>,
/// file <c>noise-suppressor.onnx</c> in the models folder) over 10 ms / 48 kHz mono frames per channel.
/// Model contract: input 0 = float frame of 480 samples (any shape whose element count is 480), outputs[0] = enhanced frame
/// of the same size; any further inputs are recurrent state tensors (zero-initialised) that receive outputs[i] of the previous
/// frame. Without a compatible model <see cref="IsAvailable"/> is false and streams are transparent pass-throughs.
/// One frame (10 ms) of latency is introduced when active.
/// </summary>
public sealed class NoiseSuppressor : INoiseSuppressor, IDisposable
{
    internal const int ModelRate = 48000;
    internal const int FrameSamples = 480;

    private readonly IModelManager _models;
    private readonly ILogger<NoiseSuppressor> _log;
    private readonly object _lock = new();
    private InferenceSession? _session;
    private bool _probed;

    public NoiseSuppressor(IModelManager models, ILogger<NoiseSuppressor> log)
    {
        _models = models;
        _log = log;
    }

    public bool IsAvailable => GetSession() is not null;

    public INoiseSuppressorStream CreateStream(int sampleRate, int channels)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        var session = GetSession();
        return session is null ? new PassthroughStream() : new ModelStream(session, sampleRate, channels, _log);
    }

    private InferenceSession? GetSession()
    {
        lock (_lock)
        {
            if (_probed) return _session;
            _probed = true;
            try
            {
                if (!_models.IsAvailable(ModelIds.NoiseSuppressor)) return null;
                var path = _models.EnsureAsync(ModelIds.NoiseSuppressor, null, CancellationToken.None).GetAwaiter().GetResult();
                var s = new InferenceSession(path);
                if (!ValidateSignature(s)) { s.Dispose(); return null; }
                _session = s;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Noise suppression model could not be loaded; audio passes through unchanged");
            }
            return _session;
        }
    }

    private bool ValidateSignature(InferenceSession s)
    {
        var ins = s.InputMetadata.ToArray();
        var outs = s.OutputMetadata.ToArray();
        if (ins.Length == 0 || ins.Length != outs.Length || ins.Any(i => i.Value.ElementType != typeof(float)))
        {
            _log.LogWarning("Noise model has an unsupported signature ({In} inputs, {Out} outputs)", ins.Length, outs.Length);
            return false;
        }
        if (ElementCount(ins[0].Value.Dimensions) != FrameSamples)
        {
            _log.LogWarning("Noise model input 0 must hold {N} samples", FrameSamples);
            return false;
        }
        return true;
    }

    private static int ElementCount(int[] dims)
    {
        var n = 1;
        foreach (var d in dims) n *= d <= 0 ? 1 : d;
        return n;
    }

    internal static int[] ConcreteDims(int[] dims) => dims.Select(d => d <= 0 ? 1 : d).ToArray();

    public void Dispose()
    {
        lock (_lock) { _session?.Dispose(); _session = null; }
    }

    private sealed class PassthroughStream : INoiseSuppressorStream
    {
        public void Process(Span<float> interleaved) { }
        public void Dispose() { }
    }

    /// <summary>Stateful linear-interpolation resampler (blocks of any length).</summary>
    internal sealed class Resampler
    {
        private readonly double _step;      // input samples per output sample
        private double _pos;                // position relative to the first sample of the next input block (can be negative)
        private float _last;

        public Resampler(int from, int to) => _step = (double)from / to;
        public bool IsIdentity => Math.Abs(_step - 1) < 1e-9;

        public void Process(ReadOnlySpan<float> input, List<float> output)
        {
            if (IsIdentity) { foreach (var v in input) output.Add(v); return; }
            while (true)
            {
                var i0 = (int)Math.Floor(_pos);
                if (i0 + 1 >= input.Length) break;
                var frac = (float)(_pos - i0);
                var a = i0 < 0 ? _last : input[i0];
                var b = input[i0 + 1];
                output.Add(a + (b - a) * frac);
                _pos += _step;
            }
            if (input.Length > 0) _last = input[^1];
            _pos -= input.Length;
        }
    }

    private sealed class ModelStream : INoiseSuppressorStream
    {
        private sealed class ChannelState
        {
            public readonly List<float> InBuf = new();
            public readonly Queue<float> OutBuf = new();
            public readonly Resampler Up, Down;
            public Dictionary<string, DenseTensor<float>> State = new();
            public ChannelState(int rate) { Up = new Resampler(rate, ModelRate); Down = new Resampler(ModelRate, rate); }
        }

        private readonly InferenceSession _s;
        private readonly int _channels;
        private readonly ChannelState[] _ch;
        private readonly string[] _inNames, _outNames;
        private readonly int[][] _inDims;
        private readonly ILogger _log;
        private readonly List<float> _tmp = new();
        private bool _faulted;

        public ModelStream(InferenceSession s, int rate, int channels, ILogger log)
        {
            _s = s; _channels = channels; _log = log;
            _inNames = s.InputMetadata.Keys.ToArray();
            _outNames = s.OutputMetadata.Keys.ToArray();
            _inDims = _inNames.Select(n => ConcreteDims(s.InputMetadata[n].Dimensions)).ToArray();
            _ch = Enumerable.Range(0, channels).Select(_ => new ChannelState(rate)).ToArray();
            foreach (var c in _ch)
            {
                for (var i = 1; i < _inNames.Length; i++) c.State[_inNames[i]] = new DenseTensor<float>(_inDims[i]);
                for (var i = 0; i < FrameSamples; i++) c.OutBuf.Enqueue(0f);           // latency pre-roll (10 ms)
            }
        }

        public void Process(Span<float> interleaved)
        {
            if (_faulted) return;
            var n = interleaved.Length / _channels;
            if (n == 0) return;
            var chan = new float[n];
            for (var c = 0; c < _channels; c++)
            {
                for (var i = 0; i < n; i++) chan[i] = interleaved[i * _channels + c];
                var st = _ch[c];
                _tmp.Clear();
                st.Up.Process(chan, _tmp);
                st.InBuf.AddRange(_tmp);
                while (st.InBuf.Count >= FrameSamples && !_faulted)
                {
                    var frame = new float[FrameSamples];
                    st.InBuf.CopyTo(0, frame, 0, FrameSamples);
                    st.InBuf.RemoveRange(0, FrameSamples);
                    var enhanced = RunFrame(st, frame);
                    _tmp.Clear();
                    st.Down.Process(enhanced, _tmp);
                    foreach (var v in _tmp) st.OutBuf.Enqueue(v);
                }
                for (var i = 0; i < n; i++)
                    interleaved[i * _channels + c] = st.OutBuf.Count > 0 ? Math.Clamp(st.OutBuf.Dequeue(), -1f, 1f) : 0f;
            }
        }

        private float[] RunFrame(ChannelState st, float[] frame)
        {
            try
            {
                var inputs = new List<NamedOnnxValue>(_inNames.Length)
                {
                    NamedOnnxValue.CreateFromTensor(_inNames[0], new DenseTensor<float>(frame.AsMemory(), _inDims[0]))
                };
                for (var i = 1; i < _inNames.Length; i++) inputs.Add(NamedOnnxValue.CreateFromTensor(_inNames[i], st.State[_inNames[i]]));
                using var results = _s.Run(inputs);
                var outs = results.ToArray();
                for (var i = 1; i < outs.Length && i < _inNames.Length; i++)
                {
                    var t = outs[i].AsTensor<float>();
                    var copy = new DenseTensor<float>(_inDims[i]);
                    var src = t.ToArray();
                    src.AsSpan(0, Math.Min(src.Length, (int)copy.Length)).CopyTo(copy.Buffer.Span);
                    st.State[_inNames[i]] = copy;
                }
                var o = outs[0].AsTensor<float>().ToArray();
                if (o.Length != FrameSamples) Array.Resize(ref o, FrameSamples);
                return o;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Noise suppression inference failed; passing audio through from now on");
                _faulted = true;
                return frame;
            }
        }

        public void Dispose() { }
    }
}
