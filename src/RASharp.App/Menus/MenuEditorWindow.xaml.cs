using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RASharp.Core.Menus;
using RASharp.Windows.Input;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDataObject = System.Windows.DataObject;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace RASharp.App.Menus;

public partial class MenuEditorWindow : Window
{
    private const string PaletteDataFormat = "RASharp.MenuEditor.Palette";
    private const string TreeNodeDataFormat = "RASharp.MenuEditor.TreeNode";
    private const double PaletteDragMinimumDistance = 8;
    private const double TreeDragMinimumDistance = 12;
    private static readonly TimeSpan TreeDragHoldDuration = TimeSpan.FromMilliseconds(180);
    private readonly Dictionary<string, MenuConfigurationFile> files;
    private MenuConfigurationFile? currentFile;
    private MenuEditorNode? selectedNode;
    private MenuEditorNode? pendingTreeDrag;
    private MenuEditorNodeKind? pendingPaletteDrag;
    private WpfPoint dragStart;
    private DateTime treeDragPressedAt;
    private bool allowClose;

    public MenuEditorWindow(string configDirectory)
    {
        InitializeComponent();
        files = new Dictionary<string, MenuConfigurationFile>(StringComparer.OrdinalIgnoreCase)
        {
            ["RunAny.ini"] = MenuConfigurationFile.Load(Path.Combine(configDirectory, "RunAny.ini"), 1),
            ["RunAny2.ini"] = MenuConfigurationFile.Load(Path.Combine(configDirectory, "RunAny2.ini"), 2),
        };
        MenuFileComboBox.ItemsSource = files.Keys;
        MenuFileComboBox.SelectedIndex = 0;
        SetEditorEnabled(false, false);
    }

    public event EventHandler? Saved;

    private void MenuFileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MenuFileComboBox.SelectedItem is not string fileName
            || !files.TryGetValue(fileName, out currentFile))
        {
            return;
        }

        selectedNode = null;
        MenuTreeView.ItemsSource = currentFile.Document.Children;
        MenuTreeView.Items.Refresh();
        ClearEditor();
        StatusTextBlock.Text = currentFile.Exists
            ? currentFile.Path
            : $"{currentFile.Path}（尚未创建，添加内容并保存后创建）";
    }

    private void MenuTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        selectedNode = e.NewValue as MenuEditorNode;
        if (selectedNode is null)
        {
            ClearEditor();
            return;
        }

        ShowNodeInEditor(selectedNode);
    }

    private void ShowNodeInEditor(MenuEditorNode node)
    {
        NodeTypeTextBlock.Text = node.Kind switch
        {
            MenuEditorNodeKind.Category => "编辑分类",
            MenuEditorNodeKind.Entry => "编辑菜单项",
            _ => "分隔线",
        };
        NameTextBox.Text = node.Name;
        ValueTextBox.Text = node.Value;
        HotKeyTextBox.Text = node.HotKey;
        RunAsAdministratorCheckBox.IsChecked = node.RunAsAdministrator;
        SetEditorEnabled(
            node.Kind != MenuEditorNodeKind.Separator,
            node.Kind == MenuEditorNodeKind.Entry);
    }

    private void Palette_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        pendingPaletteDrag = sender is FrameworkElement { Tag: string kindText }
            && Enum.TryParse<MenuEditorNodeKind>(kindText, out var kind)
                ? kind
                : null;
        dragStart = e.GetPosition(this);
    }

    private void Palette_PreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            pendingPaletteDrag = null;
            return;
        }

        if (pendingPaletteDrag is null
            || !HasExceededDragThreshold(e.GetPosition(this), PaletteDragMinimumDistance))
        {
            return;
        }

        var data = new WpfDataObject(PaletteDataFormat, pendingPaletteDrag.Value);
        pendingPaletteDrag = null;
        _ = DragDrop.DoDragDrop((DependencyObject)sender, data, WpfDragDropEffects.Copy);
    }

    private void TreeDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        pendingTreeDrag = sender is FrameworkElement { DataContext: MenuEditorNode node }
            ? node
            : null;
        dragStart = e.GetPosition(this);
        treeDragPressedAt = DateTime.UtcNow;
        if (FindTreeViewItem(sender as DependencyObject) is { } container)
        {
            container.IsSelected = true;
            _ = container.Focus();
        }

        e.Handled = true;
    }

    private void MenuTreeView_PreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            pendingTreeDrag = null;
            return;
        }

        if (pendingTreeDrag is null
            || DateTime.UtcNow - treeDragPressedAt < TreeDragHoldDuration
            || !HasExceededDragThreshold(e.GetPosition(this), TreeDragMinimumDistance))
        {
            return;
        }

        var data = new WpfDataObject(TreeNodeDataFormat, pendingTreeDrag);
        pendingTreeDrag = null;
        _ = DragDrop.DoDragDrop(MenuTreeView, data, WpfDragDropEffects.Move);
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        pendingTreeDrag = null;
        pendingPaletteDrag = null;
    }

    private void MenuTreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        pendingTreeDrag = null;
        if (FindTreeViewItem(e.OriginalSource as DependencyObject) is not { } container)
        {
            return;
        }

        container.IsSelected = true;
        _ = container.Focus();
    }

    private void MenuTreeView_DragOver(object sender, WpfDragEventArgs e)
    {
        if (currentFile is null || !TryGetDraggedItem(e.Data, out var draggedNode, out _))
        {
            e.Effects = WpfDragDropEffects.None;
            e.Handled = true;
            return;
        }

        var destination = GetDropDestination(e);
        if (draggedNode is not null && !CanMoveTo(draggedNode, destination.Parent))
        {
            e.Effects = WpfDragDropEffects.None;
            StatusTextBlock.Text = "不能把分类移动到自身或其子分类中。";
        }
        else
        {
            e.Effects = draggedNode is null ? WpfDragDropEffects.Copy : WpfDragDropEffects.Move;
            StatusTextBlock.Text = destination.Description;
        }

        e.Handled = true;
    }

    private void MenuTreeView_Drop(object sender, WpfDragEventArgs e)
    {
        if (currentFile is null
            || !TryGetDraggedItem(e.Data, out var draggedNode, out var paletteKind))
        {
            return;
        }

        var destination = GetDropDestination(e);
        if (draggedNode is not null)
        {
            if (!CanMoveTo(draggedNode, destination.Parent)
                || !currentFile.Document.MoveTo(draggedNode, destination.Parent, destination.Index))
            {
                e.Effects = WpfDragDropEffects.None;
                e.Handled = true;
                return;
            }

            StatusTextBlock.Text = $"已移动“{draggedNode.Name}”；请保存。";
        }
        else if (paletteKind is not null)
        {
            var newNode = paletteKind.Value switch
            {
                MenuEditorNodeKind.Category => MenuEditorNode.CreateCategory(),
                MenuEditorNodeKind.Entry => MenuEditorNode.CreateEntry(),
                _ => MenuEditorNode.CreateSeparator(),
            };
            currentFile.Document.Add(newNode, destination.Parent, destination.Index);
            StatusTextBlock.Text = $"已添加“{newNode.DisplayText}”；请保存。";
        }

        if (destination.InsertInside && destination.Container is not null)
        {
            destination.Container.IsExpanded = true;
        }

        MenuTreeView.Items.Refresh();
        e.Handled = true;
    }

    private void TreeContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var moveRange = selectedNode is null ? null : GetSiblingMoveRange(selectedNode);
        var canMoveUp = moveRange is not null && moveRange.Index > moveRange.MinimumIndex;
        var canMoveDown = moveRange is not null && moveRange.Index < moveRange.MaximumIndex;
        ContextMoveTopMenuItem.IsEnabled = canMoveUp;
        ContextMoveUpMenuItem.IsEnabled = canMoveUp;
        ContextMoveDownMenuItem.IsEnabled = canMoveDown;
        ContextMoveBottomMenuItem.IsEnabled = canMoveDown;
        ContextDeleteMenuItem.IsEnabled = currentFile is not null && selectedNode is not null;
        ContextDeleteMenuItem.Header = selectedNode?.Kind switch
        {
            MenuEditorNodeKind.Category => "删除分类",
            MenuEditorNodeKind.Entry => "删除应用（菜单项）",
            MenuEditorNodeKind.Separator => "删除分隔线",
            _ => "删除",
        };
    }

    private void ContextAdd_Click(object sender, RoutedEventArgs e)
    {
        if (currentFile is null
            || sender is not System.Windows.Controls.MenuItem { Tag: string kindText }
            || !Enum.TryParse<MenuEditorNodeKind>(kindText, out var kind))
        {
            return;
        }

        var (parent, index) = GetContextAddDestination();
        var node = kind switch
        {
            MenuEditorNodeKind.Category => MenuEditorNode.CreateCategory(),
            MenuEditorNodeKind.Entry => MenuEditorNode.CreateEntry(),
            _ => MenuEditorNode.CreateSeparator(),
        };
        currentFile.Document.Add(node, parent, index);
        MenuTreeView.Items.Refresh();
        SelectNodeInTree(node);
        StatusTextBlock.Text = $"已添加“{node.DisplayText}”；请保存。";
    }

    private void ContextMoveTop_Click(object sender, RoutedEventArgs e) =>
        MoveSelectedNode(toBoundary: true, direction: -1, "已置顶");

    private void ContextMoveUp_Click(object sender, RoutedEventArgs e) =>
        MoveSelectedNode(toBoundary: false, direction: -1, "已上移");

    private void ContextMoveDown_Click(object sender, RoutedEventArgs e) =>
        MoveSelectedNode(toBoundary: false, direction: 1, "已下移");

    private void ContextMoveBottom_Click(object sender, RoutedEventArgs e) =>
        MoveSelectedNode(toBoundary: true, direction: 1, "已置底");

    private void MoveSelectedNode(bool toBoundary, int direction, string status)
    {
        if (currentFile is null || selectedNode is null
            || GetSiblingMoveRange(selectedNode) is not { } moveRange)
        {
            return;
        }

        var destination = toBoundary
            ? direction < 0 ? moveRange.MinimumIndex : moveRange.MaximumIndex
            : moveRange.Index + direction;
        if (!currentFile.Document.MoveWithinSiblings(selectedNode, destination))
        {
            return;
        }

        MenuTreeView.Items.Refresh();
        SelectNodeInTree(selectedNode);
        StatusTextBlock.Text = $"{status}“{selectedNode.DisplayText}”；请保存。";
    }

    private (MenuEditorNode? Parent, int Index) GetContextAddDestination()
    {
        if (currentFile is null || selectedNode is null)
        {
            return (null, currentFile?.Document.Children.Count ?? 0);
        }

        if (selectedNode.Kind == MenuEditorNodeKind.Category)
        {
            return (selectedNode, selectedNode.Children.Count);
        }

        var siblings = selectedNode.Parent?.Children ?? currentFile.Document.Children;
        return (selectedNode.Parent, siblings.IndexOf(selectedNode) + 1);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (currentFile is null || selectedNode is null)
        {
            return;
        }

        var childNotice = selectedNode.Children.Count > 0 ? "及其全部子项" : string.Empty;
        if (System.Windows.MessageBox.Show(
                $"确定删除“{selectedNode.DisplayText}”{childNotice}吗？",
                "RASharp 编辑菜单",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _ = currentFile.Document.Remove(selectedNode);
        selectedNode = null;
        MenuTreeView.Items.Refresh();
        ClearEditor();
        StatusTextBlock.Text = "已删除；请保存。";
    }

    private void ApplyNode_Click(object sender, RoutedEventArgs e)
    {
        if (ApplySelectedNode())
        {
            StatusTextBlock.Text = "节点内容已更新；请保存。";
        }
    }

    private bool ApplySelectedNode()
    {
        if (currentFile is null || selectedNode is null
            || selectedNode.Kind == MenuEditorNodeKind.Separator)
        {
            return true;
        }

        var name = NameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            return ShowValidationError("名称不能为空。", NameTextBox);
        }

        var hotKey = HotKeyTextBox.Text.Trim();
        if (hotKey.Length > 0 && !HotKeyGesture.TryParse(hotKey, out _))
        {
            return ShowValidationError("全局热键格式无效。", HotKeyTextBox);
        }

        selectedNode.Name = name;
        selectedNode.HotKey = hotKey;
        if (selectedNode.Kind == MenuEditorNodeKind.Entry)
        {
            var value = ValueTextBox.Text.Trim();
            if (value.Length == 0)
            {
                return ShowValidationError("命令、网址或内容不能为空。", ValueTextBox);
            }

            selectedNode.Value = value;
            selectedNode.RunAsAdministrator = RunAsAdministratorCheckBox.IsChecked == true;
        }

        currentFile.Document.MarkDirty(selectedNode);
        MenuTreeView.Items.Refresh();
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveAll();

    private bool SaveAll()
    {
        if (!ApplySelectedNode())
        {
            return false;
        }

        try
        {
            var savedCount = 0;
            foreach (var file in files.Values.Where(file => file.Document.IsDirty))
            {
                file.Save();
                savedCount++;
            }

            StatusTextBlock.Text = savedCount == 0 ? "没有需要保存的更改。" : $"已保存 {savedCount} 个菜单文件。";
            if (savedCount > 0)
            {
                Saved?.Invoke(this, EventArgs.Empty);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = System.Windows.MessageBox.Show(
                exception.Message,
                "保存菜单失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (currentFile is null)
        {
            return;
        }

        if (currentFile.Document.IsDirty
            && System.Windows.MessageBox.Show(
                "重新载入会丢弃当前文件尚未保存的修改，是否继续？",
                "RASharp 编辑菜单",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var fileName = Path.GetFileName(currentFile.Path);
        files[fileName] = MenuConfigurationFile.Load(currentFile.Path, currentFile.Document.MenuNumber);
        currentFile = files[fileName];
        MenuTreeView.ItemsSource = currentFile.Document.Children;
        MenuTreeView.Items.Refresh();
        ClearEditor();
        StatusTextBlock.Text = "已从磁盘重新载入。";
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (currentFile is null || !SaveAll())
        {
            return;
        }

        if (!File.Exists(currentFile.Path))
        {
            currentFile.Save();
        }

        _ = Process.Start(new ProcessStartInfo(currentFile.Path) { UseShellExecute = true });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose || files.Values.All(file => !file.Document.IsDirty))
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            "菜单有尚未保存的修改。是否保存后关闭？",
            "RASharp 编辑菜单",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (result == MessageBoxResult.Yes && !SaveAll())
        {
            e.Cancel = true;
            return;
        }

        allowClose = true;
    }

    private void ClearEditor()
    {
        NodeTypeTextBlock.Text = "请选择左侧节点";
        foreach (var textBox in new[] { NameTextBox, ValueTextBox, HotKeyTextBox })
        {
            textBox.Clear();
        }

        RunAsAdministratorCheckBox.IsChecked = false;
        SetEditorEnabled(false, false);
    }

    private void SetEditorEnabled(bool commonEnabled, bool entryEnabled)
    {
        NameTextBox.IsEnabled = commonEnabled;
        HotKeyTextBox.IsEnabled = commonEnabled;
        ValueTextBox.IsEnabled = entryEnabled;
        RunAsAdministratorCheckBox.IsEnabled = entryEnabled;
        ApplyNodeButton.IsEnabled = commonEnabled;
    }

    private bool HasExceededDragThreshold(WpfPoint current, double minimumDistance) =>
        Math.Abs(current.X - dragStart.X) >= Math.Max(minimumDistance, SystemParameters.MinimumHorizontalDragDistance)
        || Math.Abs(current.Y - dragStart.Y) >= Math.Max(minimumDistance, SystemParameters.MinimumVerticalDragDistance);

    private void SelectNodeInTree(MenuEditorNode node)
    {
        var path = new Stack<MenuEditorNode>();
        for (var current = node; current is not null; current = current.Parent)
        {
            path.Push(current);
        }

        ItemsControl owner = MenuTreeView;
        TreeViewItem? container = null;
        while (path.TryPop(out var pathNode))
        {
            owner.UpdateLayout();
            container = owner.ItemContainerGenerator.ContainerFromItem(pathNode) as TreeViewItem;
            if (container is null)
            {
                selectedNode = node;
                ShowNodeInEditor(node);
                return;
            }

            if (path.Count > 0)
            {
                container.IsExpanded = true;
                container.UpdateLayout();
                owner = container;
            }
        }

        if (container is not null)
        {
            container.IsSelected = true;
            _ = container.Focus();
            container.BringIntoView();
        }
    }

    private SiblingMoveRange? GetSiblingMoveRange(MenuEditorNode node)
    {
        if (currentFile is null)
        {
            return null;
        }

        var siblings = node.Parent?.Children ?? currentFile.Document.Children;
        var index = siblings.IndexOf(node);
        if (index < 0)
        {
            return null;
        }

        var minimumIndex = 0;
        var maximumIndex = siblings.Count - 1;
        if (node.Parent is null)
        {
            var rootEntryCount = siblings.Count(item => item.Kind != MenuEditorNodeKind.Category);
            if (node.Kind == MenuEditorNodeKind.Category)
            {
                minimumIndex = rootEntryCount;
            }
            else
            {
                maximumIndex = rootEntryCount - 1;
            }
        }

        return new SiblingMoveRange(index, minimumIndex, maximumIndex);
    }

    private DropDestination GetDropDestination(WpfDragEventArgs e)
    {
        var container = FindTreeViewItem(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not MenuEditorNode target || currentFile is null)
        {
            return new DropDestination(
                null,
                currentFile?.Document.Children.Count ?? 0,
                "放到菜单根级末尾",
                null,
                false);
        }

        var point = e.GetPosition(container);
        var insertInside = target.Kind == MenuEditorNodeKind.Category
            && point.Y >= container.ActualHeight * 0.25
            && point.Y <= container.ActualHeight * 0.75;
        if (insertInside)
        {
            return new DropDestination(
                target,
                target.Children.Count,
                $"放入分类“{target.Name}”",
                container,
                true);
        }

        var siblings = target.Parent?.Children ?? currentFile.Document.Children;
        var targetIndex = siblings.IndexOf(target);
        var insertAfter = point.Y > container.ActualHeight / 2;
        return new DropDestination(
            target.Parent,
            targetIndex + (insertAfter ? 1 : 0),
            $"放到“{target.DisplayText}”{(insertAfter ? "之后" : "之前")}",
            container,
            false);
    }

    private static bool TryGetDraggedItem(
        System.Windows.IDataObject data,
        out MenuEditorNode? node,
        out MenuEditorNodeKind? paletteKind)
    {
        node = data.GetDataPresent(TreeNodeDataFormat)
            ? data.GetData(TreeNodeDataFormat) as MenuEditorNode
            : null;
        paletteKind = data.GetDataPresent(PaletteDataFormat)
            && data.GetData(PaletteDataFormat) is MenuEditorNodeKind kind
                ? kind
                : null;
        return node is not null || paletteKind is not null;
    }

    private static bool CanMoveTo(MenuEditorNode node, MenuEditorNode? newParent)
    {
        for (var ancestor = newParent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, node))
            {
                return false;
            }
        }

        return true;
    }

    private static MenuEditorNode? FindTreeNode(DependencyObject? source) =>
        FindTreeViewItem(source)?.DataContext as MenuEditorNode;

    private static TreeViewItem? FindTreeViewItem(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TreeViewItem item)
            {
                return item;
            }
        }

        return null;
    }

    private static bool ShowValidationError(string message, System.Windows.Controls.Control control)
    {
        _ = System.Windows.MessageBox.Show(
            message,
            "RASharp 编辑菜单",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        control.Focus();
        return false;
    }

    private sealed record DropDestination(
        MenuEditorNode? Parent,
        int Index,
        string Description,
        TreeViewItem? Container,
        bool InsertInside);

    private sealed record SiblingMoveRange(int Index, int MinimumIndex, int MaximumIndex);
}
