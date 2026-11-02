namespace IFactory.Interaction.EasyClient;
internal static class ChannelOptions
{
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
    internal static int PositiveInt(Dictionary<string, string> options, string key, int fallback)
    {
        if (!options.TryGetValue(key, out var text)) return fallback;
        if (!int.TryParse(text, out int value) || value <= 0) throw new ArgumentException($"{key} must be positive.");
        return value;
    }
}
