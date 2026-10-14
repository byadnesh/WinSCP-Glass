using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinScpGlass.Models;
using WinScpGlass.ViewModels;

namespace WinScpGlass.Views;

public partial class FilePanelView : UserControl
{
    private const string PanelDragFormat = "WinScpGlass.PanelDrag";

    private sealed record PanelDrag(FilePanelViewModel Source, IReadOnlyList<FileEntry> Items);

    private FilePanelViewModel? _vm;
    private string _sortKey = "Name";
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    private Point _dragStart;
    private bool _maybeDrag;
    private FileEntry? _pendingSingleSelect;

    public FilePanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private FilePanelViewModel Vm => _vm!;
    private SessionViewModel Session => Vm.Owner;

    public void FocusList()
    {
        FileGrid.Focus();
        if (FileGrid.SelectedItem == null && FileGrid.Items.Count > 0) FileGrid.SelectedIndex = 0;
        if (FileGrid.SelectedItem != null && FileGrid.ItemContainerGenerator.ContainerFromItem(FileGrid.SelectedItem) is DataGridRow row)
        {
            row.Focus();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.Reloaded -= OnReloaded;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = e.NewValue as FilePanelViewModel;
        if (_vm == null) return;

        _vm.Reloaded += OnReloaded;
        _vm.PropertyChanged += OnVmPropertyChanged;
        DetailColumn.Header = _vm.DetailHeader;
        DropText.Text = _vm.IsRemote ? "Drop to upload here" : "Drop to download here";
        ApplySort();
        AnimateActive(_vm.IsActive, instant: true);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilePanelViewModel.IsActive)) AnimateActive(Vm.IsActive, instant: false);
    }

    private void AnimateActive(bool active, bool instant)
    {
        var duration = instant ? TimeSpan.Zero : TimeSpan.FromMilliseconds(300);
        ActiveRim.BeginAnimation(OpacityProperty, new DoubleAnimation(active ? 0.9 : 0, duration));
        HeaderOrbActive.BeginAnimation(OpacityProperty, new DoubleAnimation(active ? 0.85 : 0, duration));
    }

    // ------------------------------------------------------------------ sorting & reload

    private void OnReloaded(string? selectName)
    {
        ApplySort();
        CrumbScroller.ScrollToRightEnd();
        if (selectName == null) return;
        var match = Vm.Items.FirstOrDefault(i => string.Equals(i.Name, selectName, StringComparison.Ordinal));
        if (match == null) return;
        FileGrid.SelectedItem = match;
        FileGrid.ScrollIntoView(match);
    }

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (_vm == null) return;
        e.Handled = true;
        string key = e.Column.SortMemberPath;
        _sortDirection = key == _sortKey && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _sortKey = key;
        ApplySort();
    }

    private void ApplySort()
    {
        foreach (var column in FileGrid.Columns)
        {
            column.SortDirection = column.SortMemberPath == _sortKey ? _sortDirection : null;
        }
        if (CollectionViewSource.GetDefaultView(FileGrid.ItemsSource) is ListCollectionView view)
        {
            view.CustomSort = new FileEntryComparer(_sortKey, _sortDirection);
        }
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null) return;
        Vm.UpdateSelection(FileGrid.SelectedItems.Cast<FileEntry>().ToList());
    }

    // ------------------------------------------------------------------ activation

    private void OnAnyMouseDown(object sender, MouseButtonEventArgs e) => _vm?.Activate();

    private void OnAnyFocus(object sender, KeyboardFocusChangedEventArgs e) => _vm?.Activate();

    // ------------------------------------------------------------------ keyboard

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        var mods = Keyboard.Modifiers;
        // Alt shortcuts arrive as Key.System.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool handled = true;

        switch (key)
        {
            case Key.Enter when mods == ModifierKeys.None:
                if (FileGrid.SelectedItem is FileEntry entry) _ = Vm.Open(entry);
                break;
            case Key.Back:
            case Key.Up when mods == ModifierKeys.Alt:
                Vm.GoUpCommand.Execute(null);
                break;
            case Key.Left when mods == ModifierKeys.Alt:
                Vm.GoBackCommand.Execute(null);
                break;
            case Key.Right when mods == ModifierKeys.Alt:
                Vm.GoForwardCommand.Execute(null);
                break;
            case Key.F2:
                Session.RenameCommand.Execute(null);
                break;
            case Key.Delete:
                Session.DeleteCommand.Execute(null);
                break;
            case Key.F5:
                Vm.RefreshCommand.Execute(null);
                break;
            case Key.N when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                Session.NewFolderCommand.Execute(null);
                break;
            case Key.U when mods == ModifierKeys.Control:
                Session.TransferSelectionCommand.Execute(null);
                break;
            case Key.F when mods == ModifierKeys.Control:
                Keyboard.Focus(FilterBox);
                break;
            case Key.L when mods == ModifierKeys.Control:
                Vm.BeginEditPathCommand.Execute(null);
                break;
            case Key.Tab when mods == ModifierKeys.None:
                FindParent<SessionView>(this)?.FocusOther(this);
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    private void FilterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Escape)
        {
            Vm.FilterText = "";
            FocusList();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Enter)
        {
            FocusList();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ path editing

    private void CrumbBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        // Clicking the empty part of the bar switches to typing a path.
        if (FindParent<Button>(e.OriginalSource as DependencyObject) == null) Vm.BeginEditPathCommand.Execute(null);
    }

    private void PathBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (PathBox.IsVisible)
        {
            Dispatcher.BeginInvoke(() =>
            {
                Keyboard.Focus(PathBox);
                PathBox.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Enter)
        {
            Vm.CommitPathCommand.Execute(null);
            FocusList();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm.CancelEditPathCommand.Execute(null);
            FocusList();
            e.Handled = true;
        }
    }

    private void PathBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm == null) return;
        if (Vm.IsEditingPath) Vm.CancelEditPathCommand.Execute(null);
    }

    // ------------------------------------------------------------------ mouse, drag & drop

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        _dragStart = e.GetPosition(this);
        _maybeDrag = row != null;
        _pendingSingleSelect = null;
        if (row == null) return;

        if (e.ClickCount == 2 && row.Item is FileEntry entry)
        {
            _maybeDrag = false;
            e.Handled = true;
            _ = Vm.Open(entry);
            return;
        }

        // Keep a multi-selection intact so it can be dragged; collapse it on release instead.
        if (row.IsSelected && FileGrid.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _pendingSingleSelect = row.Item as FileEntry;
            FileGrid.Focus();
            e.Handled = true;
        }
    }

    private void Grid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        if (_pendingSingleSelect != null)
        {
            FileGrid.SelectedItem = _pendingSingleSelect;
            _pendingSingleSelect = null;
        }
        _maybeDrag = false;
    }

    private void Grid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm == null) return;
        if (!_maybeDrag || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _maybeDrag = false;
        _pendingSingleSelect = null;
        var items = FileGrid.SelectedItems.Cast<FileEntry>().Where(i => !i.IsDrive).ToList();
        if (items.Count == 0) return;

        var data = new DataObject();
        data.SetData(PanelDragFormat, new PanelDrag(Vm, items));
        if (!Vm.IsRemote)
        {
            // Local files can also be dropped into Explorer or other apps.
            data.SetData(DataFormats.FileDrop, items.Select(i => i.FullPath).ToArray());
        }
        DragDrop.DoDragDrop(FileGrid, data, DragDropEffects.Copy);
    }

    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is { IsSelected: false })
        {
            FileGrid.SelectedItem = row.Item;
        }
        FileGrid.Focus();
    }

    private bool CanAccept(IDataObject data)
    {
        if (data.GetData(PanelDragFormat) is PanelDrag drag) return drag.Source != Vm;
        return Vm.IsRemote && data.GetDataPresent(DataFormats.FileDrop);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_vm == null) return;
        bool accept = CanAccept(e.Data);
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        Vm.IsDropTarget = accept;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (_vm == null) return;
        // DragLeave also fires when moving between child elements; only clear when truly leaving.
        var position = e.GetPosition(this);
        if (position.X <= 0 || position.Y <= 0 || position.X >= ActualWidth || position.Y >= ActualHeight)
        {
            Vm.IsDropTarget = false;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_vm == null) return;
        Vm.IsDropTarget = false;
        e.Handled = true;
        if (e.Data.GetData(PanelDragFormat) is PanelDrag drag)
        {
            if (drag.Source != Vm) _ = Session.DropAsync(drag.Source, drag.Items, Vm);
        }
        else if (Vm.IsRemote && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _ = Session.DropExternalAsync(paths);
        }
    }

    // ------------------------------------------------------------------ context menu

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        int count = Vm.Selected.Count;
        bool any = count > 0 && Vm.Selected.All(i => !i.IsDrive);
        MenuOpen.IsEnabled = count == 1;
        MenuTransfer.Header = Vm.IsRemote ? "Download" : "Upload";
        UiPropsSetIcon(MenuTransfer, Vm.IsRemote ? "" : "");
        MenuTransfer.IsEnabled = any;
        MenuRename.IsEnabled = count == 1 && any;
        MenuDelete.IsEnabled = any;
        MenuNewFolder.IsEnabled = Vm.CanModifyHere;
        MenuCopyPath.IsEnabled = count > 0;
    }

    private static void UiPropsSetIcon(DependencyObject target, string glyph) => Controls.UiProps.SetIcon(target, glyph);

    private void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (FileGrid.SelectedItem is FileEntry entry) _ = Vm.Open(entry);
    }

    private void MenuTransfer_Click(object sender, RoutedEventArgs e) => _vm?.Owner.TransferSelectionCommand.Execute(null);
    private void MenuRename_Click(object sender, RoutedEventArgs e) => _vm?.Owner.RenameCommand.Execute(null);
    private void MenuNewFolder_Click(object sender, RoutedEventArgs e) => _vm?.Owner.NewFolderCommand.Execute(null);
    private void MenuDelete_Click(object sender, RoutedEventArgs e) => _vm?.Owner.DeleteCommand.Execute(null);
    private void MenuRefresh_Click(object sender, RoutedEventArgs e) => _vm?.RefreshCommand.Execute(null);

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var paths = string.Join(Environment.NewLine, Vm.Selected.Select(i => i.FullPath));
        if (paths.Length == 0) return;
        try
        {
            Clipboard.SetText(paths);
            App.Shell.Notify("Path copied", Tone.Success);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            App.Shell.Notify("The clipboard is busy, try again", Tone.Warning);
        }
    }

    internal static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T match) return match;
            child = child is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }
}
