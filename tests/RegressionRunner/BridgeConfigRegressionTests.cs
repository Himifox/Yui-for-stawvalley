using System.Reflection;
using System.Text.Json;
using YuiToIssho;

internal static class BridgeConfigRegressionTests
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "yui-config-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.json");
        try
        {
            const string original = "{ /* user comment */ \"EnableNekoBridge\":true, \"EnableExperimentalFeatures\":false, \"CommandModeButton\":\"F6\", \"FutureSetting\":{\"nested\":[1,true,\"keep\"]}, \"nekobridgetoken\":\"\", }";
            File.WriteAllText(path, original);
            (string token, bool generated) = NekoBridgeConfig.EnsurePrivateToken(path);
            Assert.True(generated && token.Length == 48);
            using (JsonDocument config = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Assert.True(config.RootElement.GetProperty("EnableNekoBridge").GetBoolean());
                Assert.True(!config.RootElement.GetProperty("EnableExperimentalFeatures").GetBoolean());
                Assert.Equal("F6", config.RootElement.GetProperty("CommandModeButton").GetString());
                Assert.Equal("keep", config.RootElement.GetProperty("FutureSetting").GetProperty("nested")[2].GetString());
                Assert.Equal(token, config.RootElement.GetProperty("nekobridgetoken").GetString());
                Assert.True(!config.RootElement.TryGetProperty("NekoBridgeToken", out _));
            }
            byte[] preserved = File.ReadAllBytes(path);
            (string retained, bool regenerated) = NekoBridgeConfig.EnsurePrivateToken(path);
            Assert.Equal(token, retained);
            Assert.True(!regenerated && preserved.SequenceEqual(File.ReadAllBytes(path)));

            foreach (string invalid in new[] { "{invalid", "[]", "null", "{\"NekoBridgeToken\":42}", "{\"NekoBridgeToken\":\"\",\"nekobridgetoken\":\"\"}" })
            {
                File.WriteAllText(path, invalid);
                bool rejected = false;
                try { NekoBridgeConfig.EnsurePrivateToken(path); }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException) { rejected = true; }
                Assert.True(rejected && File.ReadAllText(path) == invalid, "Invalid config must remain untouched.");
            }
            File.WriteAllText(path, original);
            // Windows denies replacing a file held without delete sharing, exercising
            // the real atomic-promotion failure without touching user configuration.
            if (OperatingSystem.IsWindows())
            {
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert.Throws<IOException>(() => NekoBridgeConfig.EnsurePrivateToken(path));
                Assert.Equal(original, File.ReadAllText(path));
                Assert.Equal(1, Directory.GetFiles(directory).Length);
            }

            var leisure = new CompanionLeisureCoordinator(null!, null!, null!, null!, null!);
            Assert.Equal(long.MinValue + 2, leisure.AllocateSyntheticOccupantId(new[] { long.MinValue, long.MinValue + 1 }));
            FieldInfo counter = typeof(CompanionLeisureCoordinator).GetField("nextSyntheticOccupantId", BindingFlags.Instance | BindingFlags.NonPublic)!;
            counter.SetValue(leisure, -1L);
            Assert.Equal(-1L, leisure.AllocateSyntheticOccupantId(Array.Empty<long>()));
            Assert.Equal(long.MinValue + 1, leisure.AllocateSyntheticOccupantId(new[] { long.MinValue }));
            counter.SetValue(leisure, -1L);
            Assert.Equal(long.MinValue + 1, leisure.AllocateSyntheticOccupantId(new[] { -1L, long.MinValue }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("Bridge config regressions passed (unknown settings, stable keys, rejected input, atomic failure, and signed seat IDs).");
    }
}
