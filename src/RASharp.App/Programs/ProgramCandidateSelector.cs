using System.Windows.Threading;
using RASharp.Core.Programs;

namespace RASharp.App.Programs;

public sealed class ProgramCandidateSelector(Dispatcher dispatcher) : IProgramCandidateSelector
{
    public async Task<ProgramCandidate?> SelectAsync(
        string executable,
        IReadOnlyList<ProgramCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var operation = dispatcher.InvokeAsync(
            () =>
            {
                var window = new ProgramCandidateWindow(executable, candidates);
                return window.ShowDialog() == true ? window.SelectedCandidate : null;
            },
            DispatcherPriority.Normal,
            cancellationToken);

        return await operation.Task.ConfigureAwait(false);
    }
}
