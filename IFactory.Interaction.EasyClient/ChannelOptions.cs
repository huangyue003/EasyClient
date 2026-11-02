using System.Net;
namespace IFactory.Interaction.EasyClient;
internal static class ChannelOptions
{
    internal static Uri Address(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) throw new ArgumentException("An absolute channel URI is required.", nameof(text));
        return uri;
    }
    internal static IPEndPoint Endpoint(Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo) || !IPEndPoint.TryParse(uri.Authority, out var endpoint))
            throw new ArgumentException($"Invalid endpoint '{uri.Authority}'; use a literal IP address and port.");
        return endpoint;
    }
    internal static void Validate(string text)
    {
        var uri = Address(text);
        var options = Parse(uri);
        if (uri.Scheme == "sio") { _ = new SerialIOEndPoint(uri); return; }
        if (uri.Scheme != "tcp" && uri.Scheme != "udp") throw new ArgumentException("Unsupported URI scheme.");
        var local = Endpoint(uri);
        _ = ReceiveBuffer(options);
        if (uri.Scheme == "tcp")
        {
            int initial = BoundedInt(options, "ReconnectInitialMs", 200, 1, 60000);
            _ = BoundedInt(options, "ReconnectMaxMs", 10000, initial, 60000);
        }
        if (uri.Scheme == "udp" && (!options.TryGetValue("RemoteEndPoint", out var value) || !IPEndPoint.TryParse(value, out var remote) || remote.AddressFamily != local.AddressFamily))
            throw new ArgumentException("Invalid RemoteEndPoint.");
    }
    internal static Dictionary<string, string> Parse(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0])) throw new ArgumentException($"Invalid query parameter: {part}");
            if (!result.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1]))) throw new ArgumentException($"Duplicate query parameter: {pair[0]}");
        }
        return result;
    }
    internal static int ReceiveBuffer(Dictionary<string, string> options) => BoundedInt(options, "ReceiceBuffer", 6144, 1, 1048576);
    internal static int PositiveInt(Dictionary<string, string> options, string key, int fallback) => BoundedInt(options, key, fallback, 1, int.MaxValue);
    internal static int BoundedInt(Dictionary<string, string> options, string key, int fallback, int min, int max)
    {
        if (!options.TryGetValue(key, out var text)) return fallback;
        if (!int.TryParse(text, out int value) || value < min || value > max) throw new ArgumentException($"{key} must be in [{min}, {max}].");
        return value;
    }
}
