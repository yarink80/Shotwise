namespace Shotwise.Vision.Stitching;

/// <summary>
/// Blob store for stitched strips: keeps data in memory up to a budget and spills the rest to a private temp directory
/// so very long captures do not exhaust RAM. Not thread-safe (the stitcher serialises access).
/// </summary>
internal sealed class FrameStore : IDisposable
{
    private readonly long _memoryBudget;
    private readonly string _root;
    private readonly Dictionary<int, byte[]> _mem = new();
    private readonly Dictionary<int, string> _files = new();
    private long _memUsed;
    private int _next;
    private string? _dir;

    public FrameStore(string tempRoot, long memoryBudgetBytes)
    {
        _root = tempRoot;
        _memoryBudget = memoryBudgetBytes;
    }

    public int SpilledCount => _files.Count;

    public int Add(byte[] data)
    {
        var id = _next++;
        if (_memUsed + data.Length <= _memoryBudget)
        {
            _mem[id] = data;
            _memUsed += data.Length;
        }
        else
        {
            if (_dir is null)
            {
                _dir = Path.Combine(_root, "stitch-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_dir);
            }
            var path = Path.Combine(_dir, id + ".bin");
            File.WriteAllBytes(path, data);
            _files[id] = path;
        }
        return id;
    }

    public byte[] Get(int id)
        => _mem.TryGetValue(id, out var d) ? d : File.ReadAllBytes(_files[id]);

    public void Dispose()
    {
        _mem.Clear();
        _files.Clear();
        if (_dir is not null)
        {
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
            _dir = null;
        }
    }
}
