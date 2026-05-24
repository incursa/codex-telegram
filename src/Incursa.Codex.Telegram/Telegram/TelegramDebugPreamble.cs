using System.Globalization;
using System.Text;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Runtime switch for operator-facing Telegram message debug preambles.
/// </summary>
internal interface ITelegramDebugPreambleMode
{
    /// <summary>
    /// Gets a value indicating whether debug preambles should be included on Telegram text messages.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets the current configuration-backed default.
    /// </summary>
    bool ConfiguredDefaultEnabled { get; }

    /// <summary>
    /// Gets the in-memory runtime override, when one has been set by a Telegram command.
    /// </summary>
    bool? RuntimeOverrideEnabled { get; }

    /// <summary>
    /// Sets an in-memory runtime override.
    /// </summary>
    /// <param name="enabled">Whether debug preambles should be enabled.</param>
    void SetRuntimeOverride(bool enabled);

    /// <summary>
    /// Clears the in-memory runtime override so live configuration is used again.
    /// </summary>
    void ClearRuntimeOverride();
}

/// <summary>
/// Runtime/configuration-backed implementation of <see cref="ITelegramDebugPreambleMode"/>.
/// </summary>
internal sealed class TelegramDebugPreambleMode : ITelegramDebugPreambleMode
{
    private readonly IOptionsMonitor<TelegramOutboundOptions> _options;
    private readonly object _gate = new();
    private bool? _runtimeOverrideEnabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelegramDebugPreambleMode"/> class.
    /// </summary>
    /// <param name="options">Live outbound options.</param>
    public TelegramDebugPreambleMode(IOptionsMonitor<TelegramOutboundOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public bool IsEnabled => RuntimeOverrideEnabled ?? ConfiguredDefaultEnabled;

    /// <inheritdoc />
    public bool ConfiguredDefaultEnabled => _options.CurrentValue.DebugPreambleEnabled;

    /// <inheritdoc />
    public bool? RuntimeOverrideEnabled
    {
        get
        {
            lock (_gate)
            {
                return _runtimeOverrideEnabled;
            }
        }
    }

    /// <inheritdoc />
    public void SetRuntimeOverride(bool enabled)
    {
        lock (_gate)
        {
            _runtimeOverrideEnabled = enabled;
        }
    }

    /// <inheritdoc />
    public void ClearRuntimeOverride()
    {
        lock (_gate)
        {
            _runtimeOverrideEnabled = null;
        }
    }
}

/// <summary>
/// No-op debug preamble mode used by tests and low-level constructors.
/// </summary>
internal sealed class DisabledTelegramDebugPreambleMode : ITelegramDebugPreambleMode
{
    /// <summary>
    /// Gets the disabled singleton.
    /// </summary>
    public static DisabledTelegramDebugPreambleMode Instance { get; } = new();

    private DisabledTelegramDebugPreambleMode()
    {
    }

    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public bool ConfiguredDefaultEnabled => false;

    /// <inheritdoc />
    public bool? RuntimeOverrideEnabled => null;

    /// <inheritdoc />
    public void SetRuntimeOverride(bool enabled)
    {
    }

    /// <inheritdoc />
    public void ClearRuntimeOverride()
    {
    }
}

/// <summary>
/// Diagnostic metadata prepended to Telegram messages when debug preambles are enabled.
/// </summary>
/// <param name="Source">Message source, such as reply, outbound, or queued-worker.</param>
/// <param name="SessionId">Codex session/thread ID associated with the message.</param>
/// <param name="TurnId">Source Codex turn ID associated with the message.</param>
/// <param name="ActiveTurnId">Active turn ID observed for the session when the message was prepared.</param>
/// <param name="Kind">Message kind or event classification.</param>
/// <param name="MessageId">Queue or source message identifier.</param>
/// <param name="ItemCount">Number of queued source items represented by this Telegram message.</param>
/// <param name="TraceId">Debug trace correlation ID associated with the message.</param>
internal sealed record TelegramDebugMessageContext(
    string Source,
    string? SessionId = null,
    string? TurnId = null,
    string? ActiveTurnId = null,
    string? Kind = null,
    string? MessageId = null,
    int? ItemCount = null,
    string? TraceId = null);

/// <summary>
/// Formats compact debug preambles for Telegram text messages.
/// </summary>
internal static class TelegramDebugPreambleFormatter
{
    /// <summary>
    /// Prepends a compact diagnostic line to a Telegram message.
    /// </summary>
    /// <param name="conversation">Telegram destination.</param>
    /// <param name="text">Original message text.</param>
    /// <param name="context">Diagnostic context, when known.</param>
    /// <returns>Message text with a debug preamble.</returns>
    public static string Apply(TelegramConversationScope conversation, string text, TelegramDebugMessageContext? context)
    {
        TelegramDebugMessageContext effective = context ?? new TelegramDebugMessageContext("telegram");
        List<string> fields =
        [
            FormatField("source", effective.Source),
            FormatField("chat", conversation.ChatId.ToString(CultureInfo.InvariantCulture)),
            FormatField("topic", conversation.MessageThreadId?.ToString(CultureInfo.InvariantCulture) ?? "none"),
            FormatField("session", effective.SessionId),
            FormatField("turn", effective.TurnId),
        ];

        if (!string.IsNullOrWhiteSpace(effective.ActiveTurnId)
            && !string.Equals(effective.ActiveTurnId, effective.TurnId, StringComparison.Ordinal))
        {
            fields.Add(FormatField("activeTurn", effective.ActiveTurnId));
        }

        if (!string.IsNullOrWhiteSpace(effective.Kind))
        {
            fields.Add(FormatField("kind", effective.Kind));
        }

        if (effective.ItemCount is > 0)
        {
            fields.Add(FormatField("items", effective.ItemCount.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(effective.MessageId))
        {
            fields.Add(FormatField("msg", effective.MessageId));
        }

        if (!string.IsNullOrWhiteSpace(effective.TraceId))
        {
            fields.Add(FormatField("trace", effective.TraceId));
        }

        return $"[codex-debug {string.Join(' ', fields)}]{Environment.NewLine}{Environment.NewLine}{text.TrimStart()}";
    }

    private static string FormatField(string name, string? value)
        => $"{name}={Sanitize(value)}";

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        StringBuilder builder = new(value.Length);
        foreach (char ch in value.Trim())
        {
            builder.Append(char.IsWhiteSpace(ch) ? '_' : ch);
        }

        return builder.ToString();
    }
}
