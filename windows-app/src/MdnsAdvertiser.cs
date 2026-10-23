using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacBridge;

/// <summary>
/// Announces the API as "_macbridge._tcp" with Windows' built-in mDNS responder (DnsServiceRegister,
/// Windows 10 1903+), so Home Assistant and Homey find the PC like they find a Mac. No extra
/// service or library is installed. TXT: id (anonymous), model, protocol version and platform.
/// </summary>
sealed class MdnsAdvertiser : IDisposable
{
    private const uint DNS_QUERY_REQUEST_VERSION1 = 1;
    private const uint DNS_REQUEST_PENDING = 9506;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterComplete(uint status, IntPtr context, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_SERVICE_REGISTER_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr RegisterCompletionCallback;
        public IntPtr QueryContext;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName, IntPtr ip4, IntPtr ip6,
        ushort port, ushort priority, ushort weight, uint propertiesCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll")]
    private static extern void DnsServiceFreeInstance(IntPtr instance);

    // Kept alive for as long as Windows may call it.
    private static readonly RegisterComplete Callback = OnComplete;
    private IntPtr instance;
    private IntPtr request;

    public void Start(string name, int port, IReadOnlyDictionary<string, string> txt)
    {
        try
        {
            var host = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
            instance = DnsServiceConstructInstance(
                $"{Escape(name)}._macbridge._tcp.local", $"{(host.Length > 0 ? host : "pc")}.local",
                IntPtr.Zero, IntPtr.Zero, (ushort)port, 0, 0,
                (uint)txt.Count, txt.Keys.ToArray(), txt.Values.ToArray());
            if (instance == IntPtr.Zero) return;
            var value = new DNS_SERVICE_REGISTER_REQUEST
            {
                Version = DNS_QUERY_REQUEST_VERSION1,
                ServiceInstance = instance,
                RegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(Callback),
            };
            request = Marshal.AllocHGlobal(Marshal.SizeOf<DNS_SERVICE_REGISTER_REQUEST>());
            Marshal.StructureToPtr(value, request, false);
            var status = DnsServiceRegister(request, IntPtr.Zero);
            if (status != DNS_REQUEST_PENDING) Trace.WriteLine($"MacBridge: mDNS registration failed ({status})");
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            // Older Windows: pairing still works by typing the IP address.
            Trace.WriteLine("MacBridge: mDNS not available on this Windows version");
        }
    }

    public void Dispose()
    {
        if (request != IntPtr.Zero)
        {
            DnsServiceDeRegister(request, IntPtr.Zero);
            // The request block is tiny; keep it (not freed) since Windows may still read it
            // while deregistering asynchronously.
            request = IntPtr.Zero;
        }
        instance = IntPtr.Zero;
    }

    private static void OnComplete(uint status, IntPtr context, IntPtr completed)
    {
        if (status != 0) Trace.WriteLine($"MacBridge: mDNS status {status}");
        if (completed != IntPtr.Zero) DnsServiceFreeInstance(completed);
    }

    /// <summary>Dots would split the instance name into DNS labels.</summary>
    private static string Escape(string name) => name.Replace(".", "-");
}
