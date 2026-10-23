using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MacBridge;

/// <summary>
/// Request signing (protocol "MacBridge-v1"), identical to the Mac app.
///
/// The token never travels over the network. Every request carries a timestamp, a random nonce
/// and HMAC-SHA256(token, canonical request). The PC answers with a signature over the nonce and
/// the response body, so clients can tell a real MacBridge from an impostor on the LAN.
/// Status pushes to webhooks are signed the same way.
/// </summary>
static class Auth
{
    public const string TimestampHeader = "x-macbridge-timestamp";
    public const string NonceHeader = "x-macbridge-nonce";
    public const string SignatureHeader = "x-macbridge-signature";

    /// <summary>Requests older/newer than this are rejected (clock skew + replay window).</summary>
    public const double MaxSkewSeconds = 60;

    public static string RequestMessage(string method, string target, string timestamp, string nonce, byte[] body) =>
        $"MacBridge-v1\n{method}\n{target}\n{timestamp}\n{nonce}\n{Sha256Hex(body)}";

    public static string ResponseMessage(string nonce, int status, byte[] body) =>
        $"MacBridge-v1-response\n{nonce}\n{status}\n{Sha256Hex(body)}";

    public static string PushMessage(string timestamp, string nonce, byte[] body) =>
        $"MacBridge-v1-push\n{timestamp}\n{nonce}\n{Sha256Hex(body)}";

    public static string Sign(string message, string token) =>
        Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(message)));

    /// <summary>Constant-time comparison of two hex signatures.</summary>
    public static bool Matches(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a.ToLowerInvariant());
        var y = Encoding.UTF8.GetBytes(b.ToLowerInvariant());
        return x.Length == y.Length && x.Length > 0 && CryptographicOperations.FixedTimeEquals(x, y);
    }

    public static bool IsValidNonce(string nonce) =>
        nonce.Length is >= 16 and <= 64 && nonce.All(char.IsAsciiLetterOrDigit);

    public static string RandomNonce() => Hex(RandomNumberGenerator.GetBytes(16));

    public static string Sha256Hex(byte[] data) => Hex(SHA256.HashData(data));

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

/// <summary>Remembers nonces inside the replay window so a captured request cannot be sent again.</summary>
sealed class NonceCache
{
    private readonly Dictionary<string, DateTime> seen = new();
    private readonly object gate = new();
    private const int Limit = 10_000;

    /// <summary>Returns false if the nonce was already used.</summary>
    public bool Insert(string nonce)
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            if (seen.Count > Limit / 2)
            {
                foreach (var key in seen.Where(e => e.Value <= now).Select(e => e.Key).ToList()) seen.Remove(key);
            }
            if (seen.Count >= Limit) return false; // flooded: fail closed
            if (seen.TryGetValue(nonce, out var expiry) && expiry > now) return false;
            seen[nonce] = now.AddSeconds(Auth.MaxSkewSeconds * 2);
            return true;
        }
    }
}

/// <summary>Blocks addresses that keep sending bad signatures.</summary>
sealed class FailureLimiter
{
    private readonly Dictionary<string, List<DateTime>> failures = new();
    private readonly Dictionary<string, DateTime> blockedUntil = new();
    private readonly object gate = new();

    public bool IsBlocked(string host)
    {
        lock (gate)
        {
            if (!blockedUntil.TryGetValue(host, out var until)) return false;
            if (until > DateTime.UtcNow) return true;
            blockedUntil.Remove(host);
            return false;
        }
    }

    public void RecordFailure(string host)
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            var list = failures.TryGetValue(host, out var existing)
                ? existing.Where(d => (now - d).TotalSeconds < 60).ToList()
                : new List<DateTime>();
            list.Add(now);
            failures[host] = list;
            if (list.Count >= 10)
            {
                blockedUntil[host] = now.AddMinutes(5);
                failures.Remove(host);
                Trace.WriteLine($"MacBridge: blocking {host} for 5 minutes after repeated authentication failures");
            }
            if (failures.Count > 1000) failures.Clear();
            if (blockedUntil.Count > 1000)
            {
                foreach (var key in blockedUntil.Where(e => e.Value <= now).Select(e => e.Key).ToList()) blockedUntil.Remove(key);
            }
        }
    }
}
