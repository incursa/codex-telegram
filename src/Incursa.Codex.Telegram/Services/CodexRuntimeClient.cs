using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Services;

internal interface ICodexRuntimeClient : IAsyncDisposable
{
    CodexRuntimeCapabilities? Capabilities { get; }

    Task<CodexRuntimeMetadata> InitializeAsync(CancellationToken cancellationToken);

    Task<CodexThreadListResult> ListThreadsAsync(CodexThreadListOptions options, CancellationToken cancellationToken);

    Task<CodexThreadSnapshot> ReadThreadAsync(string threadId, CodexThreadReadOptions options, CancellationToken cancellationToken);

    Task<ICodexThreadHandle> StartThreadAsync(CodexThreadOptions options, CancellationToken cancellationToken);

    Task<ICodexThreadHandle> ResumeThreadAsync(string threadId, CodexThreadOptions? options, CancellationToken cancellationToken);

    Task<ICodexThreadHandle> ForkThreadAsync(string threadId, CodexThreadForkOptions options, CancellationToken cancellationToken);

    Task ArchiveThreadAsync(string threadId, CancellationToken cancellationToken);

    Task UnarchiveThreadAsync(string threadId, CancellationToken cancellationToken);

    Task<CodexModelListResult> ListModelsAsync(CodexModelListOptions options, CancellationToken cancellationToken);
}

internal sealed class CodexRuntimeClientAdapter : ICodexRuntimeClient
{
    private readonly CodexClient _client;

    public CodexRuntimeClientAdapter(CodexClient client)
    {
        _client = client;
    }

    public CodexRuntimeCapabilities? Capabilities => _client.Capabilities;

    public Task<CodexRuntimeMetadata> InitializeAsync(CancellationToken cancellationToken)
        => _client.InitializeAsync(cancellationToken);

    public Task<CodexThreadListResult> ListThreadsAsync(CodexThreadListOptions options, CancellationToken cancellationToken)
        => _client.ListThreadsAsync(options, cancellationToken);

    public Task<CodexThreadSnapshot> ReadThreadAsync(string threadId, CodexThreadReadOptions options, CancellationToken cancellationToken)
        => _client.ReadThreadAsync(threadId, options, cancellationToken);

    public async Task<ICodexThreadHandle> StartThreadAsync(CodexThreadOptions options, CancellationToken cancellationToken)
        => new CodexThreadHandle(await _client.StartThreadAsync(options, cancellationToken).ConfigureAwait(false));

    public async Task<ICodexThreadHandle> ResumeThreadAsync(string threadId, CodexThreadOptions? options, CancellationToken cancellationToken)
        => new CodexThreadHandle(await _client.ResumeThreadAsync(threadId, options, cancellationToken).ConfigureAwait(false));

    public async Task<ICodexThreadHandle> ForkThreadAsync(string threadId, CodexThreadForkOptions options, CancellationToken cancellationToken)
        => new CodexThreadHandle(await _client.ForkThreadAsync(threadId, options, cancellationToken).ConfigureAwait(false));

    public Task ArchiveThreadAsync(string threadId, CancellationToken cancellationToken)
        => _client.ArchiveThreadAsync(threadId, cancellationToken);

    public Task UnarchiveThreadAsync(string threadId, CancellationToken cancellationToken)
        => _client.UnarchiveThreadAsync(threadId, cancellationToken);

    public Task<CodexModelListResult> ListModelsAsync(CodexModelListOptions options, CancellationToken cancellationToken)
        => _client.ListModelsAsync(options, cancellationToken);

    public ValueTask DisposeAsync()
        => _client.DisposeAsync();
}

internal interface ICodexRuntimeClientFactory
{
    ICodexRuntimeClient Create(CodexClientOptions options);
}

internal sealed class CodexRuntimeClientFactory : ICodexRuntimeClientFactory
{
    public ICodexRuntimeClient Create(CodexClientOptions options)
        => new CodexRuntimeClientAdapter(new CodexClient(options));
}
