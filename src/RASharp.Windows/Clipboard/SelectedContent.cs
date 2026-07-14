namespace RASharp.Windows.Clipboard;

public sealed record SelectedContent(string Text, IReadOnlyList<string> Files)
{
    public static SelectedContent Empty { get; } = new(string.Empty, []);

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public bool HasFiles => Files.Count > 0;

    public string PrimaryText => HasFiles ? Files[0] : Text;
}
