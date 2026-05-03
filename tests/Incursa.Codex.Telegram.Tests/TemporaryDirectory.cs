namespace Incursa.Codex.Telegram.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    private TemporaryDirectory(string path)
    {
        Path = path;
        Directory.CreateDirectory(path);
    }

    public string Path { get; }

    public static TemporaryDirectory Create()
        => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-telegram-tests", Guid.NewGuid().ToString("n")));

    public string CreateDirectory(string name)
    {
        string path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return System.IO.Path.GetFullPath(path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}
