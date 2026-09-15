using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexTaskRecipeCatalogTests
{
    [Fact]
    public void UsesInspectableBuiltInsWhenNoRecipesAreConfigured()
    {
        CodexTaskRecipeCatalog catalog = new(Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions()));

        CodexTaskRecipeSnapshot recipe = Assert.Single(catalog.List(), candidate => candidate.Id == "implement-issue");

        Assert.Equal("implement-issue@1", recipe.RecipeKey);
        Assert.NotEmpty(recipe.Objective);
        Assert.Contains("Tests", recipe.ExpectedOutputs);
    }

    [Fact]
    public void CopiesConfiguredRecipesIntoAnImmutableSnapshot()
    {
        CodexTaskRecipeDefinition definition = new()
        {
            Id = "custom",
            Version = "2026.09",
            DisplayName = "Custom recipe",
            Objective = "Do one bounded thing.",
            ExpectedOutputs = ["Evidence"],
            RequiredCapabilities = ["codex-execution"],
        };
        CodexTelegramOptions options = new() { Recipes = [definition] };
        CodexTaskRecipeCatalog catalog = new(Microsoft.Extensions.Options.Options.Create(options));

        definition.DisplayName = "Changed after construction";
        definition.ExpectedOutputs[0] = "Changed evidence";
        CodexTaskRecipeSnapshot recipe = Assert.Single(catalog.List());

        Assert.Equal("Custom recipe", recipe.DisplayName);
        Assert.Equal("Evidence", Assert.Single(recipe.ExpectedOutputs));
        Assert.Equal("custom@2026.09", recipe.RecipeKey);
    }

    [Fact]
    public void ValidatorRejectsDuplicateOrUnsafeRecipeIdentity()
    {
        CodexTelegramOptions options = new()
        {
            Recipes =
            [
                new CodexTaskRecipeDefinition { Id = "same", Version = "1", DisplayName = "One", Objective = "Do it" },
                new CodexTaskRecipeDefinition { Id = "same", Version = "2", DisplayName = "Two", Objective = "Do it" },
            ],
        };

        ValidateOptionsResult result = new CodexTelegramOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("unique", string.Join("; ", result.Failures ?? []), StringComparison.OrdinalIgnoreCase);
    }
}
