using System.Globalization;
using System.Windows;
using System.Windows.Input;
using RASharp.Core.Programs;

namespace RASharp.App.Programs;

public partial class ProgramCandidateWindow : Window
{
    public ProgramCandidateWindow(string executable, IReadOnlyList<ProgramCandidate> candidates)
    {
        InitializeComponent();
        ExecutableRun.Text = executable;
        CountTextBlock.Text = $"共找到 {candidates.Count} 个有效程序";
        CandidatesListView.ItemsSource = candidates
            .Select(candidate => new CandidateView(candidate))
            .ToArray();
        CandidatesListView.SelectedIndex = candidates.Count > 0 ? 0 : -1;
    }

    public ProgramCandidate? SelectedCandidate { get; private set; }

    private void Confirm_Click(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void CandidatesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ConfirmSelection();

    private void ConfirmSelection()
    {
        if (CandidatesListView.SelectedItem is not CandidateView choice)
        {
            return;
        }

        SelectedCandidate = choice.Candidate;
        DialogResult = true;
    }

    private sealed class CandidateView(ProgramCandidate candidate)
    {
        public ProgramCandidate Candidate { get; } = candidate;

        public string Path => Candidate.Path;

        public string Version => Candidate.Version?.ToString() ?? "未知";

        public string LastWriteTime => Candidate.LastWriteTimeUtc
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    }
}
