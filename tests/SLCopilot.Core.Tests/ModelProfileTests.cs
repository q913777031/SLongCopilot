using System.Text.Json;
using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class ModelProfileTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "SLCopilot-profiles-" + Guid.NewGuid().ToString("N"));
    private string ProfilesPath => Path.Combine(temp, "profiles.json");
    private string LegacyPath => Path.Combine(temp, "settings.json");
    private ModelProfileStore Store => new(ProfilesPath, LegacyPath);
    public ModelProfileTests() => Directory.CreateDirectory(temp);

    [Fact]
    public void NewUsersHaveTwoSelectableProvidersWithoutWritingToDisk()
    {
        var manager = new ModelProfileManager(Store);
        Assert.Equal(2, manager.Profiles.Count);
        Assert.Contains(manager.Profiles, p => p.Settings.Provider == ProviderKind.Claude);
        Assert.Contains(manager.Profiles, p => p.Settings.Provider == ProviderKind.OpenAI);
        Assert.False(File.Exists(ProfilesPath));
    }

    [Fact]
    public void CommandAllowlistDefaultsPersistsAndRejectsInvalidRules()
    {
        var manager = new ModelProfileManager(Store);
        Assert.Equal(CommandAllowlist.Defaults, manager.AllowedCommands);
        // 0.5.0 之前的 profiles.json 没有该字段：读取后使用默认白名单。
        var legacy = JsonSerializer.Deserialize<ModelProfileCatalog>("{\"Version\":1,\"SelectedProfileId\":\"a\",\"Profiles\":[{\"Id\":\"a\",\"Name\":\"A\",\"Settings\":{}}]}")!;
        Assert.Equal(CommandAllowlist.Defaults, legacy.UpgradeLegacyDefaults().AllowedCommands);
        manager.SetAllowedCommands(["  dotnet   test ", "", "# note", "msbuild"]);
        Assert.Equal(new[] { "dotnet test", "msbuild" }, new ModelProfileManager(Store).AllowedCommands);
        Assert.Throws<ArgumentException>(() => manager.SetAllowedCommands(["C:\\tools\\evil.exe"]));
        Assert.Equal(new[] { "dotnet test", "msbuild" }, new ModelProfileManager(Store).AllowedCommands);
        manager.SetAllowedCommands([]);
        Assert.Empty(new ModelProfileManager(Store).AllowedCommands);
    }

    [Fact]
    public void DefaultsSelectSonnetMediumAndAutoApprove()
    {
        var manager = new ModelProfileManager(Store);
        Assert.Equal("claude-sonnet-5-5", manager.Current.Settings.Model);
        Assert.Equal("medium", manager.Current.Settings.Effort);
        Assert.True(manager.AutoApprove);
        // 旧版 profiles.json 没有 AutoApprove 字段：读取后为开启。
        var legacy = JsonSerializer.Deserialize<ModelProfileCatalog>("{\"Version\":1,\"SelectedProfileId\":\"a\",\"Profiles\":[{\"Id\":\"a\",\"Name\":\"A\",\"Settings\":{}}]}")!;
        Assert.True(legacy.AutoApprove);
        manager.SetAutoApprove(false);
        Assert.False(new ModelProfileManager(Store).AutoApprove);
        Assert.True(legacy.SaveBeforeAgentRun);
        Assert.True(manager.SaveBeforeAgentRun);
        manager.SetSaveBeforeAgentRun(false);
        Assert.False(new ModelProfileManager(Store).SaveBeforeAgentRun);
    }

    [Fact]
    public void LegacyCustomGatewayIsMigratedWithoutChangingOriginalSettings()
    {
        var settings = new ProviderSettings { Endpoint = "https://gateway.example/v1", Model = "custom-model", KeyEnvironmentVariable = "MY_KEY", Effort = "", MaxOutputTokens = 8000 };
        new SettingsStore(LegacyPath).Save(settings);
        var before = File.ReadAllText(LegacyPath);
        var manager = new ModelProfileManager(Store);
        Assert.Equal(settings, manager.Current.Settings);
        Assert.Equal("Imported settings", manager.Current.Name);
        manager.SaveCurrent("Work gateway", manager.Current.Settings);
        Assert.Equal(before, File.ReadAllText(LegacyPath));
        Assert.Equal(settings, new ModelProfileManager(Store).Current.Settings);
    }

    [Fact]
    public void SwitchingSavesDraftAndRestoresEveryProviderFieldAfterRestart()
    {
        var manager = new ModelProfileManager(Store);
        var draft = manager.Current.Settings with { Endpoint = "https://gateway.example/v1", Model = "code-model", KeyEnvironmentVariable = "WORK_KEY", Effort = "low", MaxOutputTokens = 12345 };
        manager.Select("openai", "Work", draft);
        Assert.Equal(ProviderKind.OpenAI, manager.Current.Settings.Provider);
        var restarted = new ModelProfileManager(Store);
        Assert.Equal("openai", restarted.Current.Id);
        restarted.Select("claude", restarted.Current.Name, restarted.Current.Settings);
        Assert.Equal("Work", restarted.Current.Name);
        Assert.Equal(draft, restarted.Current.Settings);
    }

    [Fact]
    public void AddCloneRenameAndRemovePersistIndependently()
    {
        var manager = new ModelProfileManager(Store);
        manager.Add(manager.Current.Name, manager.Current.Settings);
        var addedId = manager.Current.Id;
        manager.SaveCurrent("My other model", manager.Current.Settings with { Model = "another-model" });
        Assert.Equal(3, manager.Profiles.Count);
        Assert.Equal("gpt-4.1", manager.Profiles.Single(p => p.Id == "openai").Settings.Model);
        Assert.Equal(addedId, new ModelProfileManager(Store).Current.Id);
        manager.RemoveCurrent();
        Assert.DoesNotContain(manager.Profiles, p => p.Id == addedId);
        Assert.Equal(2, new ModelProfileManager(Store).Profiles.Count);
    }

    [Fact]
    public void InvalidDraftPreventsSwitchWithoutChangingDiskOrSelection()
    {
        var manager = new ModelProfileManager(Store);
        manager.SaveCurrent("Claude", manager.Current.Settings);
        var before = File.ReadAllText(ProfilesPath);
        Assert.Throws<ArgumentException>(() => manager.Select("openai", "Claude", manager.Current.Settings with { Endpoint = "http://invalid.example" }));
        Assert.Equal("claude", manager.Current.Id);
        Assert.Equal(before, File.ReadAllText(ProfilesPath));
    }

    [Fact]
    public void SaveFailureLeavesActiveProfileUnchanged()
    {
        Directory.CreateDirectory(ProfilesPath);
        var manager = new ModelProfileManager(Store);
        var error = Record.Exception(() => manager.Select("openai", "Claude", manager.Current.Settings));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal("claude", manager.Current.Id);
    }

    [Fact]
    public void LastProfileCannotBeRemoved()
    {
        var manager = new ModelProfileManager(Store);
        manager.RemoveCurrent();
        Assert.Single(manager.Profiles);
        Assert.Throws<ArgumentException>(() => manager.RemoveCurrent());
    }

    [Fact]
    public void DuplicateNamesAndUnknownSelectionAreRejected()
    {
        var manager = new ModelProfileManager(Store);
        Assert.Throws<ArgumentException>(() => manager.SaveCurrent(" OPENAI ", manager.Current.Settings));
        Assert.Throws<ArgumentException>(() => manager.Select("unknown", manager.Current.Name, manager.Current.Settings));
        Assert.Equal("claude", manager.Current.Id);
    }

    [Fact]
    public void CorruptProfilesDoNotSilentlyFallBackToLegacyOrDefaults()
    {
        System.IO.File.WriteAllText(ProfilesPath, "{bad json}");
        Assert.Throws<JsonException>(() => Store.Load());
        Assert.Equal("{bad json}", File.ReadAllText(ProfilesPath));
    }

    [Fact]
    public void UnknownSchemaAndMissingSelectionAreRejected()
    {
        Assert.Throws<ArgumentException>(() => (ModelProfileCatalog.Defaults() with { Version = 2 }).Validate());
        Assert.Throws<ArgumentException>(() => (ModelProfileCatalog.Defaults() with { SelectedProfileId = "missing" }).Validate());
    }

    public void Dispose() => Directory.Delete(temp, recursive: true);
}
