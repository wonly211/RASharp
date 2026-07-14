using System.Windows;
using System.Windows.Input;
using RASharp.Windows.Menus;

namespace RASharp.App.Settings;

public partial class IconPickerWindow : Window
{
    public IconPickerWindow(string sourcePath, IReadOnlyList<IconChoice> choices)
    {
        InitializeComponent();
        SourcePathTextBlock.Text = sourcePath;
        CountTextBlock.Text = $"共 {choices.Count} 个图标资源";
        IconChoicesListBox.ItemsSource = choices;
        IconChoicesListBox.SelectedIndex = choices.Count > 0 ? 0 : -1;
    }

    public IconChoice? SelectedChoice { get; private set; }

    private void Confirm_Click(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void IconChoicesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ConfirmSelection();

    private void ConfirmSelection()
    {
        if (IconChoicesListBox.SelectedItem is not IconChoice choice)
        {
            return;
        }

        SelectedChoice = choice;
        DialogResult = true;
    }
}
