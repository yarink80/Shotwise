using System.Security.Cryptography;
namespace Shotwise.Platform;
/// <summary>Bounded, one-time pairing grants and expiring bearer sessions. Tokens are retained only as hashes.</summary>
public sealed class PairingTokenStore(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _pairings = new();
    private readonly Dictionary<string, (DateTimeOffset Expires, string? Device)> _sessions = new();
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var p in _pairings.Where(p => p.Value <= now).ToArray()) _pairings.Remove(p.Key);
        foreach (var p in _sessions.Where(p => p.Value.Expires <= now).ToArray()) _sessions.Remove(p.Key);
    }
    public (string Token, DateTimeOffset Expires) Create(TimeSpan validity)
    {
        if (validity <= TimeSpan.Zero || validity > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(validity));
        lock (_gate)
        {
            Prune(); if (_pairings.Count >= 64) throw new InvalidOperationException("Too many pending pairings.");
            var token = NewToken(); var expiry = _clock.GetUtcNow() + validity; _pairings.Add(Hash(token), expiry); return (token, expiry);
        }
    }
    public string? Exchange(string? token, string? device)
    {
        if (token is null || token.Length != 64) return null;
        lock (_gate)
        {
            Prune(); if (_sessions.Count >= 64 || !_pairings.Remove(Hash(token), out _)) return null;
            var session = NewToken(); _sessions[Hash(session)] = (_clock.GetUtcNow().AddHours(12), device is null ? null : device[..Math.Min(128, device.Length)]); return session;
        }
    }
    public bool Authorize(string? token, out string? device)
    {
        device = null; if (token is null || token.Length != 64) return false;
        lock (_gate) { Prune(); if (!_sessions.TryGetValue(Hash(token), out var session)) return false; device = session.Device; return true; }
    }
    public bool CanPair(string? token)
    {
        if (token is null || token.Length != 64) return false;
        lock (_gate) { Prune(); return _pairings.ContainsKey(Hash(token)); }
    }
    public void Clear() { lock (_gate) { _pairings.Clear(); _sessions.Clear(); } }
}
public static class MobileFileRules
{
    public const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    public static string? MediaType(string filename) => Path.GetExtension(filename).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".heic" => "image/heic", ".webp" => "image/webp", ".gif" => "image/gif", ".mp4" => "video/mp4", ".mov" => "video/quicktime", _ => null
    };
}
