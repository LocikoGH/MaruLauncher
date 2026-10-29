using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MaruLauncher;

public sealed record SanitizedText(bool IsSafeToInclude, byte[] Content, bool Redacted);

public static class SecureArchiveContent
{
    private const int MaximumTextFileSize = 10 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".json5", ".toml", ".properties", ".cfg", ".ini", ".yaml", ".yml", ".xml", ".txt", ".conf"
    };

    private static readonly Regex SecretAssignment = new(
        @"(?i)(?<key>[\w.""'-]*(?:token|password|passwd|secret|api[_-]?key|client[_-]?secret|authorization|credential)[\w.""'-]*\s*[:=]\s*).*",
        RegexOptions.Compiled);
    private static readonly Regex XmlSecretElement = new(
        @"(?is)<(?<tag>[A-Za-z0-9_.:-]*(?:token|password|passwd|secret|api[_-]?key|authorization|credential)[A-Za-z0-9_.:-]*)\b[^>]*>.*?</\k<tag>\s*>",
        RegexOptions.Compiled);

    public static bool IsTextCandidate(string path) =>
        TextExtensions.Contains(Path.GetExtension(path));

    public static SanitizedText SanitizeTextFile(string path)
    {
        if (!IsTextCandidate(path))
            throw new ArgumentException("Ожидался текстовый файл.", nameof(path));
        var info = new FileInfo(path);
        if (info.Length > MaximumTextFileSize)
            return new SanitizedText(false, [], false);

        string text;
        try
        {
            text = StrictUtf8.GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException)
        {
            return new SanitizedText(false, [], false);
        }

        var redacted = false;
        if (string.Equals(info.Extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            JsonNode? json;
            try
            {
                json = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                return new SanitizedText(false, [], false);
            }
            redacted = RedactJson(json);
            if (redacted)
                text = json?.ToJsonString() ?? "null";
        }
        else
        {
            var xmlSanitized = XmlSecretElement.Replace(text, match =>
            {
                redacted = true;
                return $"<{match.Groups["tag"].Value}>[REDACTED]</{match.Groups["tag"].Value}>";
            });
            text = SecretAssignment.Replace(xmlSanitized, match =>
            {
                redacted = true;
                return match.Groups["key"].Value + "[REDACTED]";
            });
        }

        return new SanitizedText(true, StrictUtf8.GetBytes(text), redacted);
    }

    private static bool RedactJson(JsonNode? node)
    {
        var changed = false;
        if (node is JsonObject objectNode)
        {
            foreach (var (key, value) in objectNode.ToArray())
            {
                if (ContainsSecretName(key))
                {
                    objectNode[key] = "[REDACTED]";
                    changed = true;
                }
                else
                {
                    changed |= RedactJson(value);
                }
            }
        }
        else if (node is JsonArray arrayNode)
        {
            foreach (var item in arrayNode)
                changed |= RedactJson(item);
        }
        return changed;
    }

    private static bool ContainsSecretName(string key) =>
        key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("passwd", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("api-key", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("credential", StringComparison.OrdinalIgnoreCase);
}
