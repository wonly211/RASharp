using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
    private const double TreeDragMinimumDistance = 7;
    private static readonly TimeSpan CategoryHoverExpandDelay = TimeSpan.FromMilliseconds(500);
    private readonly Dictionary<string, MenuConfigurationFile> files;
    private readonly Dictionary<string, MenuEditHistory> histories;
    private MenuConfigurationFile? currentFile;
    private MenuEditorNode? selectedNode;
    private MenuEditorNode? pendingTreeDrag;
    private MenuEditorNodeKind? pendingPaletteDrag;
    private WpfPoint dragStart;
    private MenuDragAdorner? dragAdorner;
    private TreeViewItem? hoverExpandContainer;
    private DateTime hoverExpandStartedAt;
    private bool allowClose;

    public MenuEditorWindow(string configDirectory)
    {
        InitializeComponent();
        files = new Dictionary<string, MenuConfigurationFile>(StringComparer.OrdinalIgnoreCase)
        {
            ["RunAny.ini"] = MenuConfigurationFile.Load(Path.Combine(configDirectory, "RunAny.ini"), 1),
            ["RunAny2.ini"] = MenuConfigurationFile.Load(Path.Combine(configDirectory, "RunAny2.ini"), 2),
        };
        histories = files.Keys.ToDictionary(
            fileName => fileName,
            _ => new MenuEditHistory(),
            StringComparer.OrdinalIgnoreCase);
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
        UpdateCommandStates();
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
            UpdateCommandStates();
            return;
        }

        ShowNodeInEditor(selectedNode);
        UpdateCommandStates();
    }

    private void ShowNodeInEditor(MenuEditorNode node)
    {
        NodeTypeTextBlock.Text = node.Kind switch
        {
            MenuEditorNodeKind.Category => "编辑分类",
            MenuEditorNodeKind.Entry => "编辑菜单项",
            MenuEditorNodeKind.LevelSeparator => "层级复位线（-）",
            _ => "普通分隔线（|）",
        };
        NameTextBox.Text = node.Name;
        ValueTextBox.Text = node.Value;
        HotKeyTextBox.Text = node.HotKey;
        RunAsAdministratorCheckBox.IsChecked = node.RunAsAdministrator;
        SetEditorEnabled(
            node.Kind is MenuEditorNodeKind.Category or MenuEditorNodeKind.Entry,
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
        try
        {
            _ = DragDrop.DoDragDrop((DependencyObject)sender, data, WpfDragDropEffects.Copy);
        }
        finally
        {
            ClearDragVisuals();
        }
    }

    private void TreeDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        pendingTreeDrag = sender is FrameworkElement { DataContext: MenuEditorNode node }
            ? node
            : null;
        dragStart = e.GetPosition(this);
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
            || !HasExceededDragThreshold(e.GetPosition(this), TreeDragMinimumDistance))
        {
            return;
        }

        var data = new WpfDataObject(TreeNodeDataFormat, pendingTreeDrag);
        pendingTreeDrag = null;
        try
        {
            _ = DragDrop.DoDragDrop(MenuTreeView, data, WpfDragDropEffects.Move);
        }
        finally
        {
            ClearDragVisuals();
        }
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

    private void Window_QueryContinueDrag(object sender, System.Windows.QueryContinueDragEventArgs e)
    {
        if (!e.EscapePressed)
        {
            return;
        }

        e.Action = System.Windows.DragAction.Cancel;
        e.Handled = true;
        ClearDragVisuals();
        StatusTextBlock.Text = "已取消拖拽。";
    }

    private void MenuTreeView_DragOver(object sender, WpfDragEventArgs e)
    {
        if (currentFile is null || !TryGetDraggedItem(e.Data, out var draggedNode, out var paletteKind))
        {
            e.Effects = WpfDragDropEffects.None;
            e.Handled = true;
            ClearDragVisuals();
            return;
        }

        var draggedKind = draggedNode?.Kind ?? paletteKind!.Value;
        var destination = GetDropDestination(e, draggedKind, draggedNode);
        AutoScrollDuringDrag(e.GetPosition(MenuTreeView));
        UpdateHoverExpansion(destination);
        if (draggedNode is not null && !CanMoveTo(draggedNode, destination.Parent))
        {
            e.Effects = WpfDragDropEffects.None;
            StatusTextBlock.Text = "不能把分类移动到自身或其子分类中。";
            RemoveDragAdorner();
        }
        else
        {
            e.Effects = draggedNode is null ? WpfDragDropEffects.Copy : WpfDragDropEffects.Move;
            StatusTextBlock.Text = destination.Description;
            UpdateDragAdorner(
                e.GetPosition(MenuTreeView),
                destination,
                draggedNode?.DisplayText ?? GetPaletteDisplayText(paletteKind));
        }

        e.Handled = true;
    }

    private void MenuTreeView_Drop(object sender, WpfDragEventArgs e)
    {
        ClearDragVisuals();
        if (currentFile is null
            || !TryGetDraggedItem(e.Data, out var draggedNode, out var paletteKind))
        {
            return;
        }

        var draggedKind = draggedNode?.Kind ?? paletteKind!.Value;
        var destination = GetDropDestination(e, draggedKind, draggedNode);
        if (draggedNode is not null)
        {
            if (!CanMoveTo(draggedNode, destination.Parent)
                || !ExecuteMutation(() => currentFile.Document.MoveTo(
                    draggedNode,
                    destination.Parent,
                    destination.Index)))
            {
                e.Effects = WpfDragDropEffects.None;
                e.Handled = true;
                return;
            }

            StatusTextBlock.Text = $"已移动“{draggedNode.Name}”；请保存。";
            SelectNodeInTree(draggedNode);
        }
        else if (paletteKind is not null)
        {
            var newNode = paletteKind.Value switch
            {
                MenuEditorNodeKind.Category => MenuEditorNode.CreateCategory(),
                MenuEditorNodeKind.Entry => MenuEditorNode.CreateEntry(),
                MenuEditorNodeKind.LevelSeparator => MenuEditorNode.CreateLevelSeparator(),
                _ => MenuEditorNode.CreateSeparator(),
            };
            _ = ExecuteMutation(() =>
            {
                currentFile.Document.Add(newNode, destination.Parent, destination.Index);
                return true;
            });
            StatusTextBlock.Text = $"已添加“{newNode.DisplayText}”；请保存。";
            SelectNodeInTree(newNode);
        }

        if (destination.InsertInside && destination.Container is not null)
        {
            destination.Container.IsExpanded = true;
        }

        UpdateCommandStates();
        e.Handled = true;
    }

    private void MenuTreeView_DragLeave(object sender, WpfDragEventArgs e)
    {
        var point = e.GetPosition(MenuTreeView);
        if (point.X >= 0 && point.X <= MenuTreeView.ActualWidth
            && point.Y >= 0 && point.Y <= MenuTreeView.ActualHeight)
        {
            return;
        }

        ClearDragVisuals();
    }

    private void TreeContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateCommandStates();
        ContextDeleteMenuItem.Header = selectedNode?.Kind switch
        {
            MenuEditorNodeKind.Category => "删除分类",
            MenuEditorNodeKind.Entry => "删除应用（菜单项）",
            MenuEditorNodeKind.LevelSeparator => "删除层级复位线",
            MenuEditorNodeKind.Separator => "删除普通分隔线",
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

        var (parent, index) = GetContextAddDestination(kind);
        var node = kind switch
        {
            MenuEditorNodeKind.Category => MenuEditorNode.CreateCategory(),
            MenuEditorNodeKind.Entry => MenuEditorNode.CreateEntry(),
            MenuEditorNodeKind.LevelSeparator => MenuEditorNode.CreateLevelSeparator(),
            _ => MenuEditorNode.CreateSeparator(),
        };
        _ = ExecuteMutation(() =>
        {
            currentFile.Document.Add(node, parent, index);
            return true;
        });
        SelectNodeInTree(node);
        UpdateCommandStates();
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
            || GetSiblingIndex(selectedNode) is not { } currentIndex)
        {
            return;
        }

        var destination = toBoundary
            ? direction < 0 ? 0 : int.MaxValue
            : currentIndex + direction;
        var movingNode = selectedNode;
        if (!ExecuteMutation(() => currentFile.Document.MoveWithinSiblings(movingNode, destination)))
        {
            return;
        }

        SelectNodeInTree(movingNode);
        UpdateCommandStates();
        StatusTextBlock.Text = $"{status}“{movingNode.DisplayText}”；请保存。";
    }

    private void Promote_Click(object sender, RoutedEventArgs e)
    {
        if (currentFile is null || selectedNode?.Parent is not { } parent)
        {
            return;
        }

        var movingNode = selectedNode;
        var parentSiblings = parent.Parent?.Children ?? currentFile.Document.Children;
        var parentIndex = parentSiblings.IndexOf(parent);
        if (!ExecuteMutation(() => currentFile.Document.MoveTo(
                movingNode,
                parent.Parent,
                parentIndex + 1)))
        {
            return;
        }

        SelectNodeInTree(movingNode);
        UpdateCommandStates();
        StatusTextBlock.Text = $"已将“{movingNode.DisplayText}”提升一级；请保存。";
    }

    private void Demote_Click(object sender, RoutedEventArgs e)
    {
        if (currentFile is null || selectedNode is null
            || GetPreviousSiblingCategory(selectedNode) is not { } targetCategory)
        {
            return;
        }

        var movingNode = selectedNode;
        if (!ExecuteMutation(() => currentFile.Document.MoveTo(
                movingNode,
                targetCategory,
                targetCategory.Children.Count)))
        {
            return;
        }

        SelectNodeInTree(movingNode);
        UpdateCommandStates();
        StatusTextBlock.Text = $"已将“{movingNode.DisplayText}”移入“{targetCategory.Name}”；请保存。";
    }

    private void MoveToCategory_Click(object sender, RoutedEventArgs e)
    {
        if (currentFile is null || selectedNode is null)
        {
            return;
        }

        var movingNode = selectedNode;
        var window = new MoveToCategoryWindow(currentFile.Document, movingNode)
        {
            Owner = this,
        };
        if (window.ShowDialog() != true
            || !ExecuteMutation(() => currentFile.Document.MoveTo(
                movingNode,
                window.TargetParent,
                window.TargetIndex)))
        {
            return;
        }

        SelectNodeInTree(movingNode);
        UpdateCommandStates();
        StatusTextBlock.Text = $"已移动“{movingNode.DisplayText}”；请保存。";
    }

    private (MenuEditorNode? Parent, int Index) GetContextAddDestination(MenuEditorNodeKind kind)
    {
        if (currentFile is null || selectedNode is null)
        {
            return (null, currentFile?.Document.Children.Count ?? 0);
        }

        if (selectedNode.Kind == MenuEditorNodeKind.Category
            && kind != MenuEditorNodeKind.LevelSeparator)
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

        var deletedNode = selectedNode;
        if (!ExecuteMutation(() => currentFile.Document.Remove(deletedNode)))
        {
            return;
        }

        selectedNode = null;
        ClearEditor();
        UpdateCommandStates();
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
            || selectedNode.Kind is not (MenuEditorNodeKind.Category or MenuEditorNodeKind.Entry))
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

        var value = selectedNode.Value;
        var runAsAdministrator = selectedNode.RunAsAdministrator;
        if (selectedNode.Kind == MenuEditorNodeKind.Entry)
        {
            value = ValueTextBox.Text.Trim();
            if (value.Length == 0)
            {
                return ShowValidationError("命令、网址或内容不能为空。", ValueTextBox);
            }

            runAsAdministrator = RunAsAdministratorCheckBox.IsChecked == true;
        }

        var node = selectedNode;
        var hasChanges = !node.Name.Equals(name, StringComparison.Ordinal)
            || !node.HotKey.Equals(hotKey, StringComparison.Ordinal)
            || !node.Value.Equals(value, StringComparison.Ordinal)
            || node.RunAsAdministrator != runAsAdministrator;
        if (!hasChanges)
        {
            return true;
        }

        _ = ExecuteMutation(() =>
        {
            node.Name = name;
            node.HotKey = hotKey;
            if (node.Kind == MenuEditorNodeKind.Entry)
            {
                node.Value = value;
                node.RunAsAdministrator = runAsAdministrator;
            }

            currentFile.Document.MarkDirty(node);
            return true;
        });
        UpdateCommandStates();
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
                if (histories.TryGetValue(Path.GetFileName(file.Path), out var history))
                {
                    history.Clear();
                }

                savedCount++;
            }

            StatusTextBlock.Text = savedCount == 0 ? "没有需要保存的更改。" : $"已保存 {savedCount} 个菜单文件。";
            if (savedCount > 0)
            {
                Saved?.Invoke(this, EventArgs.Empty);
            }

            UpdateCommandStates();
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
        histories[fileName] = new MenuEditHistory();
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

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
            or System.Windows.Controls.ComboBox)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && key == Key.Z)
        {
            Undo();
        }
        else if (modifiers == ModifierKeys.Control && key == Key.Y)
        {
            Redo();
        }
        else if (modifiers == ModifierKeys.Alt && key == Key.Up)
        {
            MoveSelectedNode(toBoundary: false, direction: -1, "已上移");
        }
        else if (modifiers == ModifierKeys.Alt && key == Key.Down)
        {
            MoveSelectedNode(toBoundary: false, direction: 1, "已下移");
        }
        else if (modifiers == ModifierKeys.Alt && key == Key.Left)
        {
            Promote_Click(sender, e);
        }
        else if (modifiers == ModifierKeys.Alt && key == Key.Right)
        {
            Demote_Click(sender, e);
        }
        else if (modifiers == ModifierKeys.Control && key == Key.Home)
        {
            MoveSelectedNode(toBoundary: true, direction: -1, "已置顶");
        }
        else if (modifiers == ModifierKeys.Control && key == Key.End)
        {
            MoveSelectedNode(toBoundary: true, direction: 1, "已置底");
        }
        else if (modifiers == ModifierKeys.None && key == Key.Delete)
        {
            Delete_Click(sender, e);
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (currentFile is null || GetCurrentHistory() is not { UndoCount: > 0 } history)
        {
            return;
        }

        var currentState = CaptureEditorState();
        var state = history.PopUndo();
        history.PushRedo(currentState);
        RestoreEditorState(state);
        StatusTextBlock.Text = "已撤销上一步操作；请保存。";
    }

    private void Redo()
    {
        if (currentFile is null || GetCurrentHistory() is not { RedoCount: > 0 } history)
        {
            return;
        }

        var currentState = CaptureEditorState();
        var state = history.PopRedo();
        history.PushUndo(currentState, clearRedo: false);
        RestoreEditorState(state);
        StatusTextBlock.Text = "已重做上一步操作；请保存。";
    }

    private bool ExecuteMutation(Func<bool> mutation)
    {
        if (currentFile is null || GetCurrentHistory() is not { } history)
        {
            return false;
        }

        var before = CaptureEditorState();
        if (!mutation())
        {
            return false;
        }

        history.PushUndo(before, clearRedo: true);
        UpdateCommandStates();
        return true;
    }

    private EditorState CaptureEditorState() => new(
        currentFile!.Document.CreateSnapshot(),
        GetNodePath(selectedNode));

    private void RestoreEditorState(EditorState state)
    {
        if (currentFile is null)
        {
            return;
        }

        selectedNode = null;
        ClearEditor();
        currentFile.Document.RestoreSnapshot(state.Snapshot);
        MenuTreeView.ItemsSource = currentFile.Document.Children;
        MenuTreeView.UpdateLayout();
        var restoredSelection = FindNodeByPath(state.SelectedPath);
        if (restoredSelection is not null)
        {
            SelectNodeInTree(restoredSelection);
        }

        UpdateCommandStates();
    }

    private MenuEditHistory? GetCurrentHistory()
    {
        if (currentFile is null)
        {
            return null;
        }

        return histories.GetValueOrDefault(Path.GetFileName(currentFile.Path));
    }

    private int[]? GetNodePath(MenuEditorNode? node)
    {
        if (currentFile is null || node is null)
        {
            return null;
        }

        var indices = new Stack<int>();
        for (var current = node; current is not null; current = current.Parent)
        {
            var siblings = current.Parent?.Children ?? currentFile.Document.Children;
            var index = siblings.IndexOf(current);
            if (index < 0)
            {
                return null;
            }

            indices.Push(index);
        }

        return indices.ToArray();
    }

    private MenuEditorNode? FindNodeByPath(int[]? path)
    {
        if (currentFile is null || path is null || path.Length == 0)
        {
            return null;
        }

        IList<MenuEditorNode> children = currentFile.Document.Children;
        MenuEditorNode? node = null;
        foreach (var index in path)
        {
            if (index < 0 || index >= children.Count)
            {
                return null;
            }

            node = children[index];
            children = node.Children;
        }

        return node;
    }

    private void UpdateCommandStates()
    {
        var siblingIndex = selectedNode is null ? null : GetSiblingIndex(selectedNode);
        var document = currentFile?.Document;
        var canMoveTop = selectedNode is not null
            && document?.CanMoveWithinSiblings(selectedNode, 0) == true;
        var canMoveUp = selectedNode is not null && siblingIndex is not null
            && document?.CanMoveWithinSiblings(selectedNode, siblingIndex.Value - 1) == true;
        var canMoveDown = selectedNode is not null && siblingIndex is not null
            && document?.CanMoveWithinSiblings(selectedNode, siblingIndex.Value + 1) == true;
        var canMoveBottom = selectedNode is not null
            && document?.CanMoveWithinSiblings(selectedNode, int.MaxValue) == true;
        var canPromote = selectedNode?.Parent is not null;
        var canDemote = selectedNode is not null && GetPreviousSiblingCategory(selectedNode) is not null;
        var hasSelection = currentFile is not null && selectedNode is not null;
        var history = GetCurrentHistory();
        var canUndo = history?.UndoCount > 0;
        var canRedo = history?.RedoCount > 0;

        UndoButton.IsEnabled = canUndo;
        RedoButton.IsEnabled = canRedo;
        ContextUndoMenuItem.IsEnabled = canUndo;
        ContextRedoMenuItem.IsEnabled = canRedo;
        MoveTopButton.IsEnabled = canMoveTop;
        MoveUpButton.IsEnabled = canMoveUp;
        ContextMoveTopMenuItem.IsEnabled = canMoveTop;
        ContextMoveUpMenuItem.IsEnabled = canMoveUp;
        MoveDownButton.IsEnabled = canMoveDown;
        MoveBottomButton.IsEnabled = canMoveBottom;
        ContextMoveDownMenuItem.IsEnabled = canMoveDown;
        ContextMoveBottomMenuItem.IsEnabled = canMoveBottom;
        PromoteButton.IsEnabled = canPromote;
        ContextPromoteMenuItem.IsEnabled = canPromote;
        DemoteButton.IsEnabled = canDemote;
        ContextDemoteMenuItem.IsEnabled = canDemote;
        MoveToButton.IsEnabled = hasSelection;
        ContextMoveToMenuItem.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
        ContextDeleteMenuItem.IsEnabled = hasSelection;
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
        var container = FindTreeViewItemForNode(node, expandAncestors: true);
        if (container is null)
        {
            selectedNode = node;
            ShowNodeInEditor(node);
            UpdateCommandStates();
            return;
        }

        container.IsSelected = true;
        _ = container.Focus();
        container.BringIntoView();
    }

    private TreeViewItem? FindTreeViewItemForNode(
        MenuEditorNode node,
        bool expandAncestors = false)
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
                return null;
            }

            if (path.Count > 0)
            {
                if (!container.IsExpanded && !expandAncestors)
                {
                    return null;
                }

                container.IsExpanded = true;
                container.UpdateLayout();
                owner = container;
            }
        }

        return container;
    }

    private int? GetSiblingIndex(MenuEditorNode node)
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

        return index;
    }

    private DropDestination GetDropDestination(
        WpfDragEventArgs e,
        MenuEditorNodeKind draggedKind,
        MenuEditorNode? draggedNode)
    {
        var container = FindTreeViewItem(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not MenuEditorNode target || currentFile is null)
        {
            return NormalizeDropDestination(new DropDestination(
                null,
                currentFile?.Document.Children.Count ?? 0,
                "放到菜单根级末尾",
                null,
                null,
                false,
                MenuDropVisualKind.Root), draggedKind, draggedNode);
        }

        var header = GetTreeViewItemHeader(container);
        var point = e.GetPosition(header);
        var headerHeight = Math.Max(1, header.ActualHeight);
        var insertInside = target.Kind == MenuEditorNodeKind.Category
            && point.Y >= headerHeight * 0.30
            && point.Y <= headerHeight * 0.70;
        if (insertInside)
        {
            return NormalizeDropDestination(new DropDestination(
                target,
                target.Children.Count,
                $"放入分类“{target.Name}”",
                container,
                header,
                true,
                MenuDropVisualKind.Inside), draggedKind, draggedNode);
        }

        var siblings = target.Parent?.Children ?? currentFile.Document.Children;
        var targetIndex = siblings.IndexOf(target);
        var insertAfter = point.Y > headerHeight / 2;
        return NormalizeDropDestination(new DropDestination(
            target.Parent,
            targetIndex + (insertAfter ? 1 : 0),
            $"放到“{target.DisplayText}”{(insertAfter ? "之后" : "之前")}",
            container,
            header,
            false,
            insertAfter ? MenuDropVisualKind.After : MenuDropVisualKind.Before), draggedKind, draggedNode);
    }

    private DropDestination NormalizeDropDestination(
        DropDestination destination,
        MenuEditorNodeKind draggedKind,
        MenuEditorNode? draggedNode)
    {
        if (currentFile is null || destination.InsertInside)
        {
            return destination;
        }

        var siblings = destination.Parent?.Children ?? currentFile.Document.Children;
        var normalizedIndex = currentFile.Document.GetNormalizedInsertionIndex(
            draggedKind,
            destination.Parent,
            destination.Index,
            draggedNode);
        if (normalizedIndex == destination.Index)
        {
            return destination;
        }

        var groupName = draggedKind switch
        {
            MenuEditorNodeKind.Category => "分类区",
            MenuEditorNodeKind.LevelSeparator => "层级复位位置",
            _ => "应用区",
        };
        var visualSiblings = draggedNode is null
            ? siblings
            : siblings.Where(node => !ReferenceEquals(node, draggedNode)).ToList();
        if (visualSiblings.Count == 0)
        {
            return destination with { Index = normalizedIndex, Description = $"放到当前层级的{groupName}" };
        }

        var boundaryNode = normalizedIndex < visualSiblings.Count
            ? visualSiblings[normalizedIndex]
            : visualSiblings[^1];
        var boundaryContainer = FindTreeViewItemForNode(boundaryNode);
        var boundaryHeader = boundaryContainer is null ? null : GetTreeViewItemHeader(boundaryContainer);
        return destination with
        {
            Index = normalizedIndex,
            Description = $"放到当前层级的{groupName}",
            Container = boundaryContainer,
            HeaderElement = boundaryHeader,
            VisualKind = normalizedIndex < visualSiblings.Count
                ? MenuDropVisualKind.Before
                : MenuDropVisualKind.After,
        };
    }

    private void UpdateDragAdorner(
        WpfPoint cursor,
        DropDestination destination,
        string previewText)
    {
        var layer = AdornerLayer.GetAdornerLayer(MenuTreeView);
        if (layer is null)
        {
            return;
        }

        if (dragAdorner is null)
        {
            dragAdorner = new MenuDragAdorner(MenuTreeView);
            layer.Add(dragAdorner);
        }

        var bounds = destination.HeaderElement is null
            ? new Rect(
                8,
                Math.Max(8, MenuTreeView.ActualHeight - 12),
                Math.Max(40, MenuTreeView.ActualWidth - 24),
                0)
            : GetElementBounds(destination.HeaderElement);
        dragAdorner.Update(cursor, bounds, destination.VisualKind, previewText);
    }

    private Rect GetElementBounds(FrameworkElement element)
    {
        try
        {
            var topLeft = element.TransformToAncestor(MenuTreeView).Transform(new WpfPoint(0, 0));
            return new Rect(
                topLeft.X,
                topLeft.Y,
                Math.Max(element.ActualWidth, MenuTreeView.ActualWidth - topLeft.X - 14),
                Math.Max(1, element.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return new Rect(8, 8, Math.Max(40, MenuTreeView.ActualWidth - 24), 1);
        }
    }

    private void AutoScrollDuringDrag(WpfPoint point)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(MenuTreeView);
        if (scrollViewer is null)
        {
            return;
        }

        const double edge = 36;
        const double step = 14;
        if (point.Y < edge)
        {
            scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset - step));
        }
        else if (point.Y > MenuTreeView.ActualHeight - edge)
        {
            scrollViewer.ScrollToVerticalOffset(
                Math.Min(scrollViewer.ScrollableHeight, scrollViewer.VerticalOffset + step));
        }
    }

    private void UpdateHoverExpansion(DropDestination destination)
    {
        var candidate = destination.InsertInside
            && destination.Container is { IsExpanded: false }
            ? destination.Container
            : null;
        if (!ReferenceEquals(candidate, hoverExpandContainer))
        {
            hoverExpandContainer = candidate;
            hoverExpandStartedAt = DateTime.UtcNow;
            return;
        }

        if (candidate is not null
            && DateTime.UtcNow - hoverExpandStartedAt >= CategoryHoverExpandDelay)
        {
            candidate.IsExpanded = true;
            candidate.UpdateLayout();
            hoverExpandContainer = null;
        }
    }

    private void ClearDragVisuals()
    {
        RemoveDragAdorner();
        hoverExpandContainer = null;
    }

    private void RemoveDragAdorner()
    {
        if (dragAdorner is null)
        {
            return;
        }

        AdornerLayer.GetAdornerLayer(MenuTreeView)?.Remove(dragAdorner);
        dragAdorner = null;
    }

    private static FrameworkElement GetTreeViewItemHeader(TreeViewItem container)
    {
        container.ApplyTemplate();
        if (container.Template.FindName("PART_Header", container) is FrameworkElement header)
        {
            return header;
        }

        return FindVisualChild<ContentPresenter>(container) is { } presenter
            ? presenter
            : container;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result)
            {
                return result;
            }

            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private MenuEditorNode? GetPreviousSiblingCategory(MenuEditorNode node)
    {
        if (currentFile is null)
        {
            return null;
        }

        var siblings = node.Parent?.Children ?? currentFile.Document.Children;
        var index = siblings.IndexOf(node);
        return index > 0 && siblings[index - 1].Kind == MenuEditorNodeKind.Category
            ? siblings[index - 1]
            : null;
    }

    private static string GetPaletteDisplayText(MenuEditorNodeKind? kind) => kind switch
    {
        MenuEditorNodeKind.Category => "新分类",
        MenuEditorNodeKind.Entry => "新应用（菜单项）",
        MenuEditorNodeKind.LevelSeparator => "新层级复位线（-）",
        MenuEditorNodeKind.Separator => "新普通分隔线（|）",
        _ => "新节点",
    };

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
        FrameworkElement? HeaderElement,
        bool InsertInside,
        MenuDropVisualKind VisualKind);

    private sealed record EditorState(MenuEditorDocumentSnapshot Snapshot, int[]? SelectedPath);

    private sealed class MenuEditHistory
    {
        private const int MaximumEntries = 100;
        private readonly List<EditorState> undo = [];
        private readonly List<EditorState> redo = [];

        public int UndoCount => undo.Count;

        public int RedoCount => redo.Count;

        public void PushUndo(EditorState state, bool clearRedo = true)
        {
            undo.Add(state);
            Trim(undo);
            if (clearRedo)
            {
                redo.Clear();
            }
        }

        public void PushRedo(EditorState state)
        {
            redo.Add(state);
            Trim(redo);
        }

        public EditorState PopUndo() => Pop(undo);

        public EditorState PopRedo() => Pop(redo);

        public void Clear()
        {
            undo.Clear();
            redo.Clear();
        }

        private static EditorState Pop(List<EditorState> states)
        {
            var index = states.Count - 1;
            var state = states[index];
            states.RemoveAt(index);
            return state;
        }

        private static void Trim(List<EditorState> states)
        {
            if (states.Count > MaximumEntries)
            {
                states.RemoveAt(0);
            }
        }
    }
}
