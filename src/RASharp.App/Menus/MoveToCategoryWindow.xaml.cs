using System.Windows;
using System.Windows.Controls;
using RASharp.Core.Menus;

namespace RASharp.App.Menus;

public partial class MoveToCategoryWindow : Window
{
    private readonly MenuEditorDocument document;

    public MoveToCategoryWindow(MenuEditorDocument document, MenuEditorNode node)
    {
        InitializeComponent();
        this.document = document;
        DescriptionTextBlock.Text = $"选择“{node.DisplayText}”要移动到的分类。根级表示不属于任何分类。";
        var options = new List<MoveTargetOption>
        {
            new("菜单根级", null),
        };
        AppendCategories(options, document.Children, node, 0);
        TargetListBox.ItemsSource = options;
        TargetListBox.SelectedItem = options.FirstOrDefault(option => ReferenceEquals(option.Node, node.Parent))
            ?? options[0];
    }

    public MenuEditorNode? TargetParent =>
        (TargetListBox.SelectedItem as MoveTargetOption)?.Node;

    public int TargetIndex => IsInsertAtStart
        ? 0
        : TargetParent?.Children.Count ?? document.Children.Count;

    private bool IsInsertAtStart =>
        PositionComboBox.SelectedItem is ComboBoxItem { Tag: "Start" };

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (TargetListBox.SelectedItem is null)
        {
            _ = System.Windows.MessageBox.Show(
                "请选择目标分类。",
                "移动到分类",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private static void AppendCategories(
        ICollection<MoveTargetOption> options,
        IEnumerable<MenuEditorNode> nodes,
        MenuEditorNode movingNode,
        int depth)
    {
        foreach (var category in nodes.Where(node => node.Kind == MenuEditorNodeKind.Category))
        {
            if (ReferenceEquals(category, movingNode))
            {
                continue;
            }

            options.Add(new MoveTargetOption($"{new string('　', depth)}└ {category.Name}", category));
            AppendCategories(options, category.Children, movingNode, depth + 1);
        }
    }

    private sealed record MoveTargetOption(string DisplayText, MenuEditorNode? Node);
}
