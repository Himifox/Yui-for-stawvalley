using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YuiToIssho;

internal static class NekoBridgeConfig
{
    // SMAPI's typed WriteConfig would discard settings unknown to this mod version.
    // Change only the missing key, and replace the original only after a complete write.
    public static (string Token, bool Generated) EnsurePrivateToken(string configPath)
    {
        if ((File.GetAttributes(configPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The bridge config must be an ordinary file.");
        JsonObject document = JsonNode.Parse(File.ReadAllText(configPath), documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        }) as JsonObject ?? throw new InvalidDataException("The bridge config must be a JSON object.");
        string[] tokenKeys = document.Select(property => property.Key)
            .Where(key => key.Equals(nameof(ModConfig.NekoBridgeToken), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (tokenKeys.Length > 1)
            throw new InvalidDataException("The bridge config contains ambiguous key settings.");
        string key = tokenKeys.SingleOrDefault() ?? nameof(ModConfig.NekoBridgeToken);
        string? existing = document[key]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(existing))
            return (existing, false);

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        document[key] = token;
        string temporaryPath = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            File.Replace(temporaryPath, configPath, null);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (token, true);
    }
}
