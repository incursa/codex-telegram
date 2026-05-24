using System.Globalization;

namespace Incursa.Codex.Telegram.Telegram;

internal sealed record TelegramInputBundleCardContext(
    bool HasSteerableTurn,
    bool ShouldQueueForLater,
    bool AllowAttachmentSteering);

internal sealed record TelegramInputBundleAction(
    string Label,
    string CallbackPrefix,
    bool Primary = false);

internal sealed record TelegramInputBundleCardBehavior(
    string StatusText,
    string PrimaryActionText,
    string AutoDispatchActionText,
    string? AdvisoryText,
    IReadOnlyList<IReadOnlyList<TelegramInputBundleAction>> ActionRows)
{
    public static TelegramInputBundleCardBehavior Resolve(
        TelegramInputBundle bundle,
        TelegramInputBundleCardContext context)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(context);

        if (bundle.Status is not TelegramInputBundleStatus.Capturing)
        {
            string statusText = FormatTerminalStatus(bundle.Status);
            return new TelegramInputBundleCardBehavior(statusText, statusText, statusText, null, []);
        }

        List<IReadOnlyList<TelegramInputBundleAction>> rows = [];
        string? advisory = null;
        string primaryAction;
        string autoDispatchAction;

        if (context.HasSteerableTurn)
        {
            autoDispatchAction = "Queue next";
            if (bundle.Intent is TelegramInputBundleIntent.QueueNext)
            {
                List<TelegramInputBundleAction> firstRow =
                [
                    new("Queue next", "bqueue", Primary: true),
                ];
                if (bundle.Attachments.Count == 0 || context.AllowAttachmentSteering)
                {
                    firstRow.Add(new TelegramInputBundleAction("Steer current turn", "bsteer"));
                }
                else if (!string.IsNullOrWhiteSpace(bundle.CombinedText))
                {
                    firstRow.Add(new TelegramInputBundleAction("Text-only steer", "bsteertext"));
                    advisory = "Attachment steering is not supported; queue the full bundle or steer text only.";
                }

                rows.Add(firstRow);
                primaryAction = "Queue next";
            }
            else
            {
                bool attachmentSteeringBlocked = bundle.Attachments.Count > 0 && !context.AllowAttachmentSteering;
                if (attachmentSteeringBlocked)
                {
                    List<TelegramInputBundleAction> firstRow =
                    [
                        new("Queue next", "bqueue", Primary: true),
                    ];
                    if (!string.IsNullOrWhiteSpace(bundle.CombinedText))
                    {
                        firstRow.Add(new TelegramInputBundleAction("Text-only steer", "bsteertext"));
                    }

                    rows.Add(firstRow);
                    primaryAction = "Queue next";
                    advisory = "Attachment steering is not supported; queue the full bundle or steer text only.";
                }
                else
                {
                    rows.Add(
                        [
                            new TelegramInputBundleAction("Steer current turn", "bsteer", Primary: true),
                            new TelegramInputBundleAction("Queue next", "bqueue"),
                        ]);
                    primaryAction = "Steer current turn";
                }
            }
        }
        else if (context.ShouldQueueForLater)
        {
            rows.Add([new TelegramInputBundleAction("Queue next", "bqueue", Primary: true)]);
            primaryAction = "Queue next";
            autoDispatchAction = "Queue next";
            advisory = "Codex or Telegram delivery is busy; this bundle will wait for the next turn.";
        }
        else
        {
            rows.Add([new TelegramInputBundleAction("Send now", "bsend", Primary: true)]);
            primaryAction = "Send now";
            autoDispatchAction = "Send now";
        }

        rows.Add(
            [
                new TelegramInputBundleAction("Add more", "badd"),
                new TelegramInputBundleAction("Clear", "bclear"),
                new TelegramInputBundleAction("Cancel", "bcancel"),
            ]);

        string status = bundle.HasContent ? "Bundle ready" : "Capturing";
        return new TelegramInputBundleCardBehavior(status, primaryAction, autoDispatchAction, advisory, rows);
    }

    public static string Revision(TelegramInputBundle bundle)
        => bundle.UpdatedAt.UtcTicks.ToString("x", CultureInfo.InvariantCulture);

    public static string CallbackData(TelegramInputBundle bundle, string callbackPrefix)
        => $"{callbackPrefix}:{bundle.Id}|{Revision(bundle)}";

    public static TelegramInputBundleCallback ParseCallback(string value)
    {
        string[] parts = value.Split('|', 2, StringSplitOptions.TrimEntries);
        return new TelegramInputBundleCallback(
            parts.Length > 0 ? parts[0] : string.Empty,
            parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null);
    }

    private static string FormatTerminalStatus(TelegramInputBundleStatus status)
        => status switch
        {
            TelegramInputBundleStatus.Submitted => "Bundle submitted",
            TelegramInputBundleStatus.Queued => "Queued for next turn",
            TelegramInputBundleStatus.Steered => "Bundle steered",
            TelegramInputBundleStatus.Sent => "Bundle sent",
            TelegramInputBundleStatus.Cancelled => "Bundle cancelled",
            TelegramInputBundleStatus.Expired => "Bundle expired",
            _ => status.ToString(),
        };
}

internal sealed record TelegramInputBundleCallback(string BundleId, string? Revision);
