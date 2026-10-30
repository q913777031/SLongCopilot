using System.Text.Json;

namespace SLCopilot.Core;

public sealed class SettingsStore(string path)
{
    public ProviderSettings Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<ProviderSettings>(File.ReadAllText(path)) ?? new()
        : new();

    public void Save(ProviderSettings settings)
    {
        settings.RequestUri();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        Compat.MoveOverwrite(temp, path);
    }
}
