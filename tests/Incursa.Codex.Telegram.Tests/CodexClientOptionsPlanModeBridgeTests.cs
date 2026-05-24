using System.Reflection;
using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Services;

public sealed class CodexClientOptionsPlanModeBridgeTests
{
    [Fact]
    public void CopyPlanMode_ClonesPlanModeObjectWhenTheSdkExposesIt()
    {
        PropertyInfo? planModeProperty = typeof(CodexClientOptions).GetProperty("PlanMode", BindingFlags.Instance | BindingFlags.Public);
        if (planModeProperty is null)
        {
            return;
        }

        object sourcePlanMode = Activator.CreateInstance(planModeProperty.PropertyType)
            ?? throw new InvalidOperationException("Failed to create the plan-mode options instance.");
        PropertyInfo? reasoningEffortProperty = sourcePlanMode.GetType().GetProperty("ReasoningEffort", BindingFlags.Instance | BindingFlags.Public);
        if (reasoningEffortProperty is not null)
        {
            Type enumType = Nullable.GetUnderlyingType(reasoningEffortProperty.PropertyType) ?? reasoningEffortProperty.PropertyType;
            reasoningEffortProperty.SetValue(sourcePlanMode, Enum.Parse(enumType, "High", ignoreCase: true));
        }

        CodexClientOptions source = new();
        planModeProperty.SetValue(source, sourcePlanMode);

        CodexClientOptions destination = new();

        CodexClientOptionsPlanModeBridge.CopyPlanMode(source, destination);

        object? destinationPlanMode = planModeProperty.GetValue(destination);
        Assert.NotNull(destinationPlanMode);
        Assert.NotSame(sourcePlanMode, destinationPlanMode);
        Assert.Equal(sourcePlanMode, destinationPlanMode);
    }

    [Fact]
    public void ApplyReasoningEffort_PopulatesPlanModeReasoningEffortWhenTheSdkExposesIt()
    {
        PropertyInfo? planModeProperty = typeof(CodexClientOptions).GetProperty("PlanMode", BindingFlags.Instance | BindingFlags.Public);
        if (planModeProperty is null)
        {
            return;
        }

        CodexClientOptions options = new();

        CodexClientOptionsPlanModeBridge.ApplyReasoningEffort(options, "high");

        object? planMode = planModeProperty.GetValue(options);
        Assert.NotNull(planMode);

        PropertyInfo? reasoningEffortProperty = planMode.GetType().GetProperty("ReasoningEffort", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(reasoningEffortProperty);
        Assert.Equal("High", reasoningEffortProperty.GetValue(planMode)?.ToString());
    }

    [Fact]
    public void ApplyReasoningEffort_ClonesExistingPlanModeBeforeUpdatingWhenTheSdkExposesIt()
    {
        PropertyInfo? planModeProperty = typeof(CodexClientOptions).GetProperty("PlanMode", BindingFlags.Instance | BindingFlags.Public);
        if (planModeProperty is null)
        {
            return;
        }

        object existingPlanMode = Activator.CreateInstance(planModeProperty.PropertyType)
            ?? throw new InvalidOperationException("Failed to create the plan-mode options instance.");
        PropertyInfo? reasoningEffortProperty = existingPlanMode.GetType().GetProperty("ReasoningEffort", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(reasoningEffortProperty);
        Type enumType = Nullable.GetUnderlyingType(reasoningEffortProperty.PropertyType) ?? reasoningEffortProperty.PropertyType;
        reasoningEffortProperty.SetValue(existingPlanMode, Enum.Parse(enumType, "Low", ignoreCase: true));

        CodexClientOptions options = new();
        planModeProperty.SetValue(options, existingPlanMode);

        CodexClientOptionsPlanModeBridge.ApplyReasoningEffort(options, "high");

        object? updatedPlanMode = planModeProperty.GetValue(options);
        Assert.NotNull(updatedPlanMode);
        Assert.NotSame(existingPlanMode, updatedPlanMode);
        Assert.Equal("High", reasoningEffortProperty.GetValue(updatedPlanMode)?.ToString());
        Assert.Equal("Low", reasoningEffortProperty.GetValue(existingPlanMode)?.ToString());
    }
}
