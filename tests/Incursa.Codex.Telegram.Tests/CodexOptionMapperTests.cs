using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexOptionMapperTests
{
    [Fact]
    public void BuildThreadOptions_MapsKebabCaseSandboxAndApprovalValuesAsConfigOverrides()
    {
        CodexTelegramOptions options = new()
        {
            Context = new CodexContextOptions
            {
                Sandbox = "workspace-write",
                ApprovalMode = "on-request",
                ApprovalsReviewer = "user",
            },
        };

        CodexThreadOptions mapped = CodexOptionMapper.BuildThreadOptions(options, new CodexThreadContextSubmission());

        Assert.Null(mapped.Sandbox);
        Assert.Null(mapped.ApprovalPolicy);
        Assert.Equal("workspace-write", GetConfigString(mapped.Config, "sandbox_mode"));
        Assert.Equal("on-request", GetConfigString(mapped.Config, "approval_policy"));
        Assert.Equal(CodexApprovalsReviewer.User, mapped.ApprovalsReviewer);
    }

    [Fact]
    public void BuildThreadOptions_EnablesRequestUserInputFeatureForPlanModeTurns()
    {
        CodexTelegramOptions options = new();
        CodexTurnSubmission submission = new()
        {
            PlanMode = true,
        };

        CodexThreadOptions mapped = CodexOptionMapper.BuildThreadOptions(options, submission);

        CodexConfigObject features = GetConfigObject(mapped.Config, "features");
        Assert.True(GetConfigBoolean(features, "default_mode_request_user_input"));
    }

    [Fact]
    public void BuildTurnOptions_MapsTurnLevelAndFallsBackToManifestAndDefaults()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string defaultsDirectory = temp.CreateDirectory("defaults");
        string manifestDirectory = temp.CreateDirectory("manifest");
        string submissionDirectory = temp.CreateDirectory("submission");

        CodexTelegramOptions options = new()
        {
            Context = new CodexContextOptions
            {
                WorkingDirectory = defaultsDirectory,
                Model = "model-default",
                Personality = "Friendly",
                ServiceTier = "Fast",
                ApprovalsReviewer = "User",
                ReasoningEffort = "Low",
                ReasoningSummary = "Concise",
            },
        };

        CodexThreadManifestRecord manifest = new()
        {
            ThreadId = "thread-2",
            WorkingDirectory = manifestDirectory,
            Model = "model-manifest",
            Personality = "Pragmatic",
            ServiceTier = "Flex",
            ApprovalsReviewer = "GuardianSubAgent",
            ReasoningEffort = "Medium",
            ReasoningSummary = "Detailed",
        };

        CodexTurnSubmission submission = new()
        {
            WorkingDirectory = submissionDirectory,
            Model = "model-submission",
            Personality = "Friendly",
            ServiceTier = "Fast",
            ApprovalsReviewer = "User",
            ReasoningEffort = "High",
            ReasoningSummary = "Auto",
        };

        CodexTurnOptions mapped = CodexOptionMapper.BuildTurnOptions(options, submission, manifest);

        Assert.Equal(submissionDirectory, mapped.WorkingDirectory);
        Assert.Equal("model-submission", mapped.Model);
        Assert.Equal(CodexPersonality.Friendly, mapped.Personality);
        Assert.Equal(CodexServiceTier.Fast, mapped.ServiceTier);
        Assert.Equal(CodexApprovalsReviewer.User, mapped.ApprovalsReviewer);
        Assert.Equal(CodexReasoningEffort.High, mapped.Effort);
        Assert.Equal(CodexReasoningSummary.Auto, mapped.Summary);
        Assert.Null(mapped.SandboxPolicy);
        Assert.Null(mapped.ApprovalPolicy);
    }

    private static string GetConfigString(CodexConfigObject? config, string key)
    {
        Assert.NotNull(config);
        Assert.True(config.Values.TryGetValue(key, out CodexConfigValue? value));
        CodexConfigStringValue stringValue = Assert.IsType<CodexConfigStringValue>(value);
        return stringValue.Value;
    }

    private static CodexConfigObject GetConfigObject(CodexConfigObject? config, string key)
    {
        Assert.NotNull(config);
        Assert.True(config.Values.TryGetValue(key, out CodexConfigValue? value));
        return Assert.IsType<CodexConfigObject>(value);
    }

    private static bool GetConfigBoolean(CodexConfigObject config, string key)
    {
        Assert.True(config.Values.TryGetValue(key, out CodexConfigValue? value));
        CodexConfigBooleanValue booleanValue = Assert.IsType<CodexConfigBooleanValue>(value);
        return booleanValue.Value;
    }
}
