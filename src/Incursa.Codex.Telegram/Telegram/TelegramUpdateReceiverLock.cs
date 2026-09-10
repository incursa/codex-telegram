using System.Security.Cryptography;
using System.Text;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Coordinates local Telegram update receivers for one bot token. Telegram
/// delivers a polling stream per token, so a second local receiver would race
/// the configured instance and make updates appear to disappear.
/// </summary>
internal static class TelegramUpdateReceiverLock
{
    private static readonly string LockDirectory = Path.Combine(
        Path.GetTempPath(),
        "Incursa.Codex.Telegram",
        "receivers");

    internal static IDisposable Acquire(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("A Telegram bot token is required to acquire the update receiver lock.", nameof(token));
        }

        string normalized = token.Trim();
        string tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        Directory.CreateDirectory(LockDirectory);
        string path = Path.Combine(LockDirectory, $"{tokenHash}.lock");
        try
        {
            FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new Lease(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another local process already owns Telegram polling for this bot token. Stop the duplicate receiver or configure a different bot token.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException(
                $"Telegram polling could not create its local receiver lock under '{LockDirectory}'. Grant access to the temporary directory or configure a writable environment.",
                exception);
        }
    }

    private sealed class Lease : IDisposable
    {
        private FileStream? _stream;

        public Lease(FileStream stream)
        {
            _stream = stream;
        }

        public void Dispose()
        {
            FileStream? stream = Interlocked.Exchange(ref _stream, null);
            stream?.Dispose();
            // The empty lock file is harmless and retained to avoid a race
            // between a new receiver and cleanup of an old lease.
        }
    }
}
