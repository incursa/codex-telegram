using System.Reflection;
using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Services;

internal static class CodexClientOptionsPlanModeBridge
{
    private const BindingFlags InstancePropertyFlags = BindingFlags.Instance | BindingFlags.Public;
    private const string PlanModePropertyName = "PlanMode";

    public static void CopyPlanMode(CodexClientOptions source, CodexClientOptions destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        // Keep this bridge compatible with the older package fallback while still flowing the nested
        // PlanMode object whenever the updated SDK surface is present in the workspace.
        PropertyInfo? sourceProperty = typeof(CodexClientOptions).GetProperty(PlanModePropertyName, InstancePropertyFlags);
        if (sourceProperty?.CanRead != true)
        {
            return;
        }

        PropertyInfo? destinationProperty = typeof(CodexClientOptions).GetProperty(PlanModePropertyName, InstancePropertyFlags);
        if (destinationProperty?.CanWrite != true)
        {
            return;
        }

        object? planMode = sourceProperty.GetValue(source);
        if (planMode is null)
        {
            return;
        }

        destinationProperty.SetValue(destination, ClonePlanMode(planMode, destinationProperty.PropertyType));
    }

    public static void ApplyReasoningEffort(CodexClientOptions options, string? reasoningEffort)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return;
        }

        PropertyInfo? planModeProperty = typeof(CodexClientOptions).GetProperty(PlanModePropertyName, InstancePropertyFlags);
        if (planModeProperty?.CanRead != true || planModeProperty.CanWrite != true)
        {
            return;
        }

        object? planMode = planModeProperty.GetValue(options);
        if (planMode is null)
        {
            planMode = Activator.CreateInstance(planModeProperty.PropertyType);
            if (planMode is null)
            {
                return;
            }
        }
        else
        {
            planMode = ClonePlanMode(planMode, planModeProperty.PropertyType);
        }

        PropertyInfo? reasoningEffortProperty = planMode.GetType().GetProperty("ReasoningEffort", InstancePropertyFlags);
        if (reasoningEffortProperty?.CanWrite != true)
        {
            return;
        }

        if (!TryParseEnumValue(reasoningEffortProperty.PropertyType, reasoningEffort, out object? parsedValue))
        {
            return;
        }

        reasoningEffortProperty.SetValue(planMode, parsedValue);
        planModeProperty.SetValue(options, planMode);
    }

    private static object ClonePlanMode(object source, Type destinationType)
    {
        object? clone = Activator.CreateInstance(destinationType);
        if (clone is null)
        {
            throw new InvalidOperationException($"Unable to create an instance of {destinationType.FullName}.");
        }

        foreach (PropertyInfo property in destinationType.GetProperties(InstancePropertyFlags))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            property.SetValue(clone, property.GetValue(source));
        }

        return clone;
    }

    private static bool TryParseEnumValue(Type candidateType, string value, out object? parsedValue)
    {
        parsedValue = null;

        Type enumType = Nullable.GetUnderlyingType(candidateType) ?? candidateType;
        if (!enumType.IsEnum)
        {
            return false;
        }

        try
        {
            parsedValue = Enum.Parse(enumType, value, ignoreCase: true);
            return true;
        }
        catch (ArgumentException)
        {
            string normalizedValue = NormalizeEnumToken(value);
            foreach (string enumName in Enum.GetNames(enumType))
            {
                if (string.Equals(NormalizeEnumToken(enumName), normalizedValue, StringComparison.OrdinalIgnoreCase))
                {
                    parsedValue = Enum.Parse(enumType, enumName, ignoreCase: true);
                    return true;
                }
            }

            return false;
        }
    }

    private static string NormalizeEnumToken(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
