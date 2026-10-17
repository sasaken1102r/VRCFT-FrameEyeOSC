using System.Text.Json;

namespace FrameEyeOSC;

/// <summary>
/// The optional <c>FrameEyeOSCConfig.json</c> next to the module DLL, e.g. <c>{"port": 11112}</c>. A missing file means
/// the defaults; the module never writes it.
/// </summary>
/// <param name="Port">The UDP port to listen on.</param>
public sealed record FrameEyeOscConfig(int Port)
{
    /// <summary>The config file name.</summary>
    public const string FileName = "FrameEyeOSCConfig.json";

    /// <summary>The defaults (port 11112).</summary>
    public static FrameEyeOscConfig Default { get; } = new(FrameEyeOscReceiver.DefaultPort);

    /// <summary>Reads the config, falling back to the defaults for a missing file and, with a warning, a bad one.</summary>
    /// <param name="path">Full path of the config file.</param>
    /// <param name="warning">Why the file was not used, or null when it was used or is absent.</param>
    /// <returns>The config to use.</returns>
    public static FrameEyeOscConfig Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path))
            return Default;

        try
        {
            return Parse(File.ReadAllText(path), out warning);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warning = $"could not read {FileName} ({e.Message}); using port {Default.Port}";
            return Default;
        }
    }

    /// <summary>Parses the config JSON. Unknown properties are ignored; a missing <c>port</c> keeps the default.</summary>
    /// <param name="json">The file's text.</param>
    /// <param name="warning">Why the text was not used, or null.</param>
    /// <returns>The parsed config, or the defaults when the text is invalid.</returns>
    public static FrameEyeOscConfig Parse(string json, out string? warning)
    {
        warning = null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                warning = $"{FileName} is not a JSON object; using port {Default.Port}";
                return Default;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "port", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var port) ||
                    port is < 1 or > 65535)
                {
                    warning = $"\"port\" in {FileName} must be a whole number 1-65535; using port {Default.Port}";
                    return Default;
                }
                return new FrameEyeOscConfig(port);
            }
            return Default;
        }
        catch (JsonException e)
        {
            warning = $"{FileName} is not valid JSON ({e.Message}); using port {Default.Port}";
            return Default;
        }
    }
}
