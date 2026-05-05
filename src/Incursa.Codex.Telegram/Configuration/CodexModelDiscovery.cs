using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Services;
using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Configuration;

internal sealed record CodexModelCatalog(
    IReadOnlyList<CodexModelVm> Models,
    bool IsLive,
    string? Message);

internal static class CodexModelDiscovery
{
    private const int MaxVisibleModels = 12;
    private const string ClientName = "Incursa.Codex.Telegram.Setup";
    private const string ClientTitle = "Incursa Codex Telegram Setup";

    public static CodexModelCatalog CreateFallbackCatalog(string? message = null)
        => new(
            CreateFallbackModels(),
            false,
            message ?? "Live model discovery is unavailable; using curated bootstrap examples.");

    public static async Task<CodexModelCatalog> DiscoverAsync(
        LocalSettingsSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        string? executablePath = ResolveCodexExecutablePath(snapshot);
        CodexClientOptions options = new()
        {
            BackendSelection = CodexBackendSelection.AppServer,
            CodexPathOverride = executablePath,
            ClientName = ClientName,
            ClientTitle = ClientTitle,
            ClientVersion = typeof(CodexModelDiscovery).Assembly.GetName().Version?.ToString() ?? "1.0.6",
        };

        try
        {
            await using CodexClient client = new(options);
            if (!await client.IsCodexAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                return CreateFallbackCatalog($"Live model discovery is unavailable because Codex was not found at {DescribeExecutablePath(executablePath)}.");
            }

            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);

            CodexModelListResult result = await client.ListModelsAsync(new CodexModelListOptions(), cancellationToken).ConfigureAwait(false);
            List<CodexModelVm> models = result.Models
                .Select(ToModelVm)
                .OrderByDescending(model => model.IsDefault)
                .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (models.Count == 0)
            {
                return CreateFallbackCatalog("Live model discovery returned no models.");
            }

            if (models.Count > MaxVisibleModels)
            {
                int hiddenCount = models.Count - MaxVisibleModels;
                models = models.Take(MaxVisibleModels).ToList();
                return new CodexModelCatalog(
                    models,
                    true,
                    $"Live model discovery returned more than {MaxVisibleModels} models; showing the first {models.Count} and hiding {hiddenCount} others.");
            }

            return new CodexModelCatalog(
                models,
                true,
                $"Live model discovery returned {models.Count} model{(models.Count == 1 ? string.Empty : "s")} from {DescribeExecutablePath(executablePath)}.");
        }
        catch (CodexCapabilityNotSupportedException exception)
        {
            return CreateFallbackCatalog($"Live model discovery is unavailable: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CreateFallbackCatalog($"Live model discovery failed: {exception.Message}");
        }
    }

    private static IReadOnlyList<CodexModelVm> CreateFallbackModels()
        => [
            CreateFallbackModel("gpt-5.4-mini", true),
            CreateFallbackModel("gpt-5.4"),
        ];

    private static CodexModelVm CreateFallbackModel(string id, bool isDefault = false)
        => new(
            id,
            id,
            "Curated bootstrap example.",
            CodexReasoningEffort.None,
            [],
            isDefault,
            false,
            false,
            null);

    private static CodexModelVm ToModelVm(CodexModel model)
    {
        string? displayName = CodexTextFormatting.RepairUtf8Mojibake(model.DisplayName);
        return new CodexModelVm(
            model.Model,
            string.IsNullOrWhiteSpace(displayName) ? model.Model : displayName,
            CodexTextFormatting.RepairUtf8Mojibake(model.Description) ?? string.Empty,
            model.DefaultReasoningEffort,
            model.SupportedReasoningEfforts.Select(option => option.ReasoningEffort).ToArray(),
            model.IsDefault,
            model.Hidden,
            model.SupportsPersonality == true,
            CodexTextFormatting.RepairUtf8Mojibake(model.AvailabilityNux?.Message));
    }

    private static string? ResolveCodexExecutablePath(LocalSettingsSnapshot snapshot)
        => !string.IsNullOrWhiteSpace(snapshot.CodexPathOverride)
            ? snapshot.CodexPathOverride
            : !string.IsNullOrWhiteSpace(snapshot.TelegramBotCodexExecutablePath)
                ? snapshot.TelegramBotCodexExecutablePath
                : Environment.GetEnvironmentVariable("CODEX_PATH");

    private static string DescribeExecutablePath(string? value)
        => string.IsNullOrWhiteSpace(value) ? "PATH" : value;
}
