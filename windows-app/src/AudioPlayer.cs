using System.Diagnostics;
using System.Net;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace MacBridge;

/// <summary>
/// Plays a short audio clip (text-to-speech from Home Assistant). Only fetches from the same
/// computer that sent the signed request, never follows redirects, and caps size and time.
/// </summary>
sealed class AudioPlayer : IDisposable
{
    private const int MaxBytes = 20 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = MaxBytes,
    };
    private readonly object gate = new();
    private MediaPlayer? player;

    public sealed class NotAllowedException : Exception
    {
    }

    /// <param name="requester">IP of the client whose signed request asked for this.</param>
    public void Play(string url, string requester, double? volume)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !ResolvesTo(uri.DnsSafeHost, requester))
            throw new NotAllowedException();

        using var response = Client.GetAsync(uri).GetAwaiter().GetResult();
        if (response.StatusCode != HttpStatusCode.OK) throw new IOException($"HTTP {(int)response.StatusCode}");
        var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";

        var stream = new MemoryStream(bytes).AsRandomAccessStream();
        var next = new MediaPlayer { Source = MediaSource.CreateFromStream(stream, contentType) };
        if (volume is { } v) next.Volume = Math.Clamp(v, 0, 1);
        lock (gate)
        {
            player?.Dispose();
            player = next;
        }
        next.Play();
    }

    public void Stop()
    {
        lock (gate)
        {
            player?.Pause();
        }
    }

    private static bool ResolvesTo(string host, string requester)
    {
        if (!IPAddress.TryParse(requester.Split('%')[0], out var want)) return false;
        want = Normalize(want);
        try
        {
            return Dns.GetHostAddresses(host).Any(a => Normalize(a).Equals(want));
        }
        catch (System.Net.Sockets.SocketException e)
        {
            Trace.WriteLine($"MacBridge: could not resolve {host}: {e.Message}");
            return false;
        }
    }

    private static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? new IPAddress(address.GetAddressBytes()) : address;
    }

    public void Dispose()
    {
        lock (gate) player?.Dispose();
    }
}
