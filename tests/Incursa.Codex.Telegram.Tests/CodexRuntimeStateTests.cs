using Incursa.Codex.Telegram.Services;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexRuntimeStateTests
{
    [Fact]
    public void SetError_RewritesMojibakeInRuntimeErrors()
    {
        CodexRuntimeState state = new();

        state.SetError(new InvalidOperationException("The repo itself doesnΓÇÖt expose the RFC label."));

        var vm = state.ToViewModel();

        Assert.Single(vm.Errors);
        Assert.Equal("The repo itself doesn’t expose the RFC label.", vm.Errors[0]);
    }
}
