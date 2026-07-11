using SonnetDB.Copilot;
using Xunit;

namespace SonnetDB.Tests;

public sealed class CopilotModelCatalogTests
{
    [Fact]
    public void Build_WithProviderNeutralMetadata_GroupsAndDeduplicatesModels()
    {
        var catalog = CopilotModelCatalog.Build(
        [
            new OpenAiModelItem("custom-first", "Custom First", "custom"),
            new OpenAiModelItem("platform-main", "Platform Main", "custom", IsDefault: true),
            new OpenAiModelItem("local-qwen", "Local Qwen", "local"),
            new OpenAiModelItem("CUSTOM-FIRST", "Duplicate", "local"),
        ]);

        Assert.Equal("platform-main", catalog.Default);
        Assert.Equal(["platform-main", "custom-first", "local-qwen"], catalog.Candidates);

        var platform = Assert.Single(catalog.Groups, static group => group.Key == CopilotModelCatalog.PlatformDefaultGroup);
        var defaultModel = Assert.Single(platform.Models);
        Assert.True(defaultModel.IsDefault);
        Assert.Equal("Platform Main", defaultModel.DisplayName);

        var custom = Assert.Single(catalog.Groups, static group => group.Key == CopilotModelCatalog.CustomGroup);
        Assert.Equal("custom-first", Assert.Single(custom.Models).Id);

        var local = Assert.Single(catalog.Groups, static group => group.Key == CopilotModelCatalog.LocalGroup);
        Assert.Equal("local-qwen", Assert.Single(local.Models).Id);
    }

    [Fact]
    public void Build_WithoutMetadata_UsesFirstModelAsDefaultAndRemainingModelsAsCustom()
    {
        var catalog = CopilotModelCatalog.Build(
        [
            new OpenAiModelItem("default-model"),
            new OpenAiModelItem("other-model"),
        ]);

        Assert.Equal("default-model", catalog.Default);
        Assert.Equal("other-model", Assert.Single(catalog.Groups[1].Models).Id);
        Assert.Empty(catalog.Groups[2].Models);
    }
}
