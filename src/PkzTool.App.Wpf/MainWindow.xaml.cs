using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PkzTool.App.Wpf.Dialogs;
using PkzTool.App.Wpf.Theming;
using PkzTool.Core;
using PkzTool.Core.Compression;
using PkzTool.Core.Operations;
using PkzTool.Core.Settings;
using PkzTool.Core.Vfs;
using PkzTool.Core.Workspace;
using Xt1.Core.Util;

namespace PkzTool.App.Wpf;

/// <summary>
/// The single shell window. It owns the workspace (the loaded archives and their staged edits), turns
/// it into the flat, indented rows the grid shows, and drives every file operation.
/// <para>
/// The list is a <see cref="ListView"/> with a four-column <see cref="GridView"/> rather than a
/// TreeView, because the brief asks for a sortable grid whose rows still form a real folder tree. The
/// tree structure lives in the row set: a folder's children are inserted directly after it and are
/// indented by depth, so sorting and column widths behave like a grid while expansion behaves like a
/// tree.
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    private readonly PkzWorkspace _workspace = new();
    private readonly ObservableCollection<FileRow> _rows = new();
    private readonly HashSet<string> _expanded = new(VirtualPath.Comparer);
    private readonly List<FileRow> _allRows = new();

    private PkzSettings _settings = new();
    private CancellationTokenSource? _operation;
    private string _search = string.Empty;

    // Column sort state. Null means "PKZ order", which is the archive's stored order: the default and
    // the one that must survive a save untouched.
    private SortColumn _sortColumn = SortColumn.None;
    private bool _sortDescending;

    /// <summary>The four sortable columns.</summary>
    internal enum SortColumn
    {
        /// <summary>PKZ order: the stored order, folders first.</summary>
        None,
        Name,
        Size,
        Format,
        Compression,
    }

    public MainWindow()
    {
        InitializeComponent();

        _settings = App.Settings;
        _workspace.OodlePath = _settings.ResolveOodleDll();
        _workspace.Changed += OnWorkspaceChanged;

        FileList.ItemsSource = _rows;
        RestoreColumnWidths();
        RestoreWindowState();

        // Header clicks sort the view. GridViewColumnHeader.Click is a bubbling event, so it is caught
        // here on the list rather than needing a handler per column. It is attached in code because the
        // header style lives in a ResourceDictionary, which cannot carry an event handler.
        FileList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler(OnColumnHeaderClick));

        ThemeManager.Changed += OnThemeChanged;
        Loaded += (_, _) =>
        {
            ThemeManager.ApplyTitleBar(this);
            UpdateCommandStates();
            UpdateStatus();
        };
        Closing += OnClosing;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    // --- window state ----------------------------------------------------------------------

    private void RestoreColumnWidths()
    {
        ColumnName.Width = Math.Max(120, _settings.ColumnNameWidth);
        ColumnSize.Width = Math.Max(60, _settings.ColumnSizeWidth);
        ColumnFormat.Width = Math.Max(50, _settings.ColumnFormatWidth);
        ColumnCompression.Width = Math.Max(70, _settings.ColumnCompressionWidth);
    }

    private void RememberColumnWidths()
    {
        // A GridViewColumn's Width is NaN until it has been measured, so an unmeasured column must not
        // overwrite the remembered value with NaN.
        if (!double.IsNaN(ColumnName.Width)) _settings.ColumnNameWidth = ColumnName.Width;
        if (!double.IsNaN(ColumnSize.Width)) _settings.ColumnSizeWidth = ColumnSize.Width;
        if (!double.IsNaN(ColumnFormat.Width)) _settings.ColumnFormatWidth = ColumnFormat.Width;
        if (!double.IsNaN(ColumnCompression.Width)) _settings.ColumnCompressionWidth = ColumnCompression.Width;
    }

    private void RestoreWindowState()
    {
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    private void OnThemeChanged()
    {
        // The rows hold brushes resolved at read time, so they are re-read after a palette swap.
        foreach (var row in _allRows) row.Refresh();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // A headless run must never raise a prompt: it has nobody to answer one, so a modal dialog
        // there would hang the process instead of reporting the failure it was checking for.
        if (_headless)
        {
            RememberColumnWidths();
            _settings.Save();
            _workspace.Dispose();
            return;
        }

        if (!ConfirmDiscardStagedEdits("closing")) e.Cancel = true;
        else
        {
            RememberColumnWidths();
            _settings.Maximized = WindowState == WindowState.Maximized;
            _settings.Save();
            _workspace.Dispose();
        }
    }

    /// <summary>
    /// True while this window exists only for a headless check. It suppresses every prompt, so a failed
    /// check reports itself rather than blocking on a dialog nobody can dismiss.
    /// </summary>
    private bool _headless;

    // --- loading ---------------------------------------------------------------------------

    /// <summary>Open the given archives, replacing the whole view, and report any that failed.</summary>
    private void LoadArchives(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (!ConfirmDiscardStagedEdits("opening another pkz")) return;

        var loaded = RunWithProgress("Loading", token =>
        {
            var result = _workspace.Load(paths, _settings.ResolveOodleDll(), token);

            // Remember the files that loaded, newest first, so Open Recent offers them again.
            foreach (var directory in paths
                         .Where(p => !result.Failures.Any(f => string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase)))
                         .Select(Path.GetDirectoryName)
                         .Where(d => !string.IsNullOrEmpty(d))
                         .Distinct())
                _settings.LastDirectory = directory;

            foreach (var path in paths) _settings.RememberRecent(path);
            _settings.Save();
            return result;
        });

        if (loaded is null) return; // cancelled or failed

        // A file that could not be read is skipped and reported; the rest stay loaded.
        if (loaded.Failures.Count > 0)
        {
            var detail = string.Join("\n\n", loaded.Failures.Select(f => $"{Path.GetFileName(f.Path)}\n{f.Error}"));
            MessageBox.Show(this,
                loaded.Loaded == 0
                    ? $"None of the {loaded.Failed} selected file(s) could be loaded:\n\n{detail}"
                    : $"{loaded.Loaded} pkz file(s) loaded.\n\nSkipped {loaded.Failed}:\n\n{detail}",
                loaded.Loaded == 0 ? "Could not load" : "Some files were skipped",
                MessageBoxButton.OK, loaded.Loaded == 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        if (loaded.Loaded == 0)
        {
            UpdateCommandStates();
            UpdateStatus();
            return;
        }

        // A fresh load starts with the tree collapsed and the list in PKZ order.
        _expanded.Clear();
        _sortColumn = SortColumn.None;
        _sortDescending = false;
        RebuildRows();
    }

    // --- the row set -----------------------------------------------------------------------

    /// <summary>
    /// Rebuild the visible rows from the workspace: walk the tree, keep a folder's children only when
    /// it is expanded, apply the search filter, then apply the view sort.
    /// </summary>
    private void RebuildRows()
    {
        _allRows.Clear();
        CollectRows(_workspace.Root, 0);
        ApplyFilterAndSort();
    }

    private void CollectRows(VfsNode folder, int depth)
    {
        if (folder.Kind == VfsNodeKind.File)
        {
            _allRows.Add(new FileRow(folder, depth, false));
            return;
        }

        // The root itself is not a row; its children are the top level.
        if (folder.Path.Length > 0)
            _allRows.Add(new FileRow(folder, depth, _expanded.Contains(folder.Path)));

        int childDepth = folder.Path.Length > 0 ? depth + 1 : depth;
        bool expanded = folder.Path.Length == 0 || _expanded.Contains(folder.Path);
        if (!expanded) return;

        foreach (var child in folder.ChildFolders) CollectRows(child, childDepth);

        // Files come after folders so the tree reads folders-first within a folder, matching the
        // documented default order.
        foreach (var file in folder.ChildFiles) CollectRows(file, childDepth);
    }

    /// <summary>
    /// Apply the search box and the column sort to <see cref="_allRows"/>, then publish the result.
    /// <para>
    /// Filtering keeps a folder row whenever anything under it matches, so a match is never shown
    /// without the path that leads to it.
    /// </para>
    /// </summary>
    private void ApplyFilterAndSort()
    {
        IEnumerable<FileRow> rows = _allRows;

        if (_search.Length > 0)
        {
            rows = FilterRows(rows);
        }

        if (_sortColumn != SortColumn.None)
        {
            // Sorting is view-only: it reorders the rows that are shown and never touches the archive's
            // stored order, which is why a save reproduces the original layout.
            var sorted = SortRows(rows);
            rows = sorted;
        }
        else if (_search.Length > 0)
        {
            // With no sort the stored order already puts folders before files per folder, so the
            // filtered list keeps that shape without a second pass.
            rows = rows.ToList();
        }

        var materialised = rows.ToList();

        _rows.Clear();
        foreach (var row in materialised) _rows.Add(row);

        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FileList.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // A sort highlights its column header, so the current order is never a mystery.
        UpdateHeaderGlyphs();
        UpdateCommandStates();
        UpdateStatus();
    }

    /// <summary>
    /// Keep rows whose path matches the search, plus the folder rows leading to them.
    /// </summary>
    private List<FileRow> FilterRows(IEnumerable<FileRow> rows)
    {
        var all = rows.ToList();
        var keep = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in all)
        {
            if (row.IsFolder) continue;
            if (row.Node.Path.Contains(_search, StringComparison.OrdinalIgnoreCase))
            {
                keep.Add(row.Node.Key);
                // Every ancestor folder is kept so the indentation still makes sense.
                foreach (var ancestor in VirtualPath.Ancestors(row.Node.Path))
                    keep.Add(VfsNode.KeyForFolder(ancestor));
            }
        }

        // Folder rows whose whole subtree matched are already covered by the ancestor walk above; a
        // folder matching by its own name keeps everything under it.
        foreach (var row in all.Where(r => r.IsFolder))
            if (row.Node.Path.Contains(_search, StringComparison.OrdinalIgnoreCase))
                foreach (var file in row.Node.DescendantFiles())
                    keep.Add(file.Key);

        return all.Where(r => keep.Contains(r.Node.Key)).ToList();
    }

    /// <summary>
    /// Sort the visible rows by the active column, folders always before files at each level.
    /// <para>
    /// A tree is only meaningfully sorted *within* a folder, so rows are grouped by the folder that
    /// contains them and each group is sorted independently; the tree shape is preserved. A folder's
    /// own containing folder is its <c>Directory</c>, which is what makes a top-level folder a child of
    /// the root rather than a root in its own right (its <c>Path</c> is its own name, not its parent).
    /// </para>
    /// </summary>
    private List<FileRow> SortRows(IEnumerable<FileRow> rows)
    {
        var byParent = rows.GroupBy(r => r.Node.Directory)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var result = new List<FileRow>();
        AppendSorted(string.Empty);
        return result;

        void AppendSorted(string folderPath)
        {
            if (!byParent.TryGetValue(folderPath, out var children)) return;

            var folders = Comparer(children.Where(c => c.IsFolder)).ToList();
            var files = Comparer(children.Where(c => !c.IsFolder)).ToList();

            foreach (var row in folders) result.Add(row);
            foreach (var row in files) result.Add(row);

            // A folder's own children come immediately after it, and only while it is expanded (a
            // collapsed folder contributes no child rows to sort in the first place).
            foreach (var row in folders) AppendSorted(row.Node.Path);
        }

        IEnumerable<FileRow> Comparer(IEnumerable<FileRow> source)
        {
            // Folders are compared by name whatever the column, because a folder has no size, format or
            // compression: sorting them by an empty value would scramble them for no gain.
            Func<FileRow, IComparable> key = _sortColumn switch
            {
                SortColumn.Size when true => r => r.IsFolder ? string.Empty : (IComparable)r.Node.UncompressedSize,
                SortColumn.Format => r => r.IsFolder ? string.Empty : r.Format,
                SortColumn.Compression => r => r.IsFolder ? string.Empty : r.Compression,
                _ => r => r.DisplayName,
            };

            // The name column falls back to the path so two rows of the same leaf name sort predictably.
            var ordered = _sortColumn is SortColumn.Name or SortColumn.None
                ? source.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Node.Path, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(key).ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase);

            return _sortDescending ? ordered.Reverse() : ordered;
        }
    }

    /// <summary>Show the sort direction in the column headers.</summary>
    private void UpdateHeaderGlyphs()
    {
        string arrow = _sortDescending ? " \u25BE" : " \u25B4";
        ColumnName.Header = "Filename" + (_sortColumn == SortColumn.Name ? arrow : string.Empty);
        ColumnSize.Header = "Size" + (_sortColumn == SortColumn.Size ? arrow : string.Empty);
        ColumnFormat.Header = "Format" + (_sortColumn == SortColumn.Format ? arrow : string.Empty);
        ColumnCompression.Header = "Compression" + (_sortColumn == SortColumn.Compression ? arrow : string.Empty);
    }

    private void OnWorkspaceChanged() => RebuildRows();

    // --- selection and commands ------------------------------------------------------------

    /// <summary>The selected rows, folders included.</summary>
    private List<VfsNode> SelectedNodes() =>
        FileList.SelectedItems.Cast<FileRow>().Select(r => r.Node).ToList();

    /// <summary>
    /// The files an action applies to: every selected row, with a folder expanding to its subtree.
    /// </summary>
    private List<VfsNode> SelectedFiles() =>
        SelectedNodes().SelectMany(n => n.DescendantFiles()).Distinct().ToList();

    /// <summary>Enable each menu item, toolbar button and shortcut for what is selected.</summary>
    private void UpdateCommandStates()
    {
        var selection = SelectedNodes();
        bool anySelection = selection.Count > 0;
        bool single = selection.Count == 1;
        bool singleFile = single && selection[0].Kind == VfsNodeKind.File;
        bool hasFiles = SelectedFiles().Count > 0;
        bool loaded = _workspace.Archives.Count > 0;

        MenuExtract.IsEnabled = CtxExtract.IsEnabled = hasFiles;
        MenuExtractAll.IsEnabled = CtxExtractAll.IsEnabled = loaded && _workspace.AllFiles.Any();
        // Replace works on exactly one file: replacing a folder is not meaningful.
        MenuReplace.IsEnabled = CtxReplace.IsEnabled = singleFile;
        MenuRecompress.IsEnabled = CtxRecompress.IsEnabled = hasFiles;
        MenuAdd.IsEnabled = CtxAdd.IsEnabled = loaded;
        MenuRename.IsEnabled = CtxRename.IsEnabled = single &&
            (singleFile || selection[0].Kind == VfsNodeKind.Folder);
        MenuDelete.IsEnabled = CtxDelete.IsEnabled = anySelection;

        // Revert only means something when the selection actually carries a staged change.
        bool revertable = hasFiles && SelectedFiles().Any(f => f.IsModified);
        MenuRevert.IsEnabled = CtxRevert.IsEnabled = revertable;
        MenuSelectAll.IsEnabled = _rows.Count > 0;
        MenuExpandAll.IsEnabled = CtxExpand.IsEnabled = loaded;
        MenuCollapseAll.IsEnabled = CtxCollapse.IsEnabled = loaded;

        bool modified = _workspace.IsModified;
        MenuSave.IsEnabled = modified;
        MenuSaveAs.IsEnabled = loaded;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCommandStates();

    private void OnSelectAll(object sender, RoutedEventArgs e) => FileList.SelectAll();

    /// <summary>
    /// Sort by the clicked column. Clicking the active column flips the direction; clicking a different
    /// one starts ascending. Clicking "Filename" three times returns to PKZ order, which is the stored
    /// order and the only one a save preserves.
    /// </summary>
    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader header) return;
        if (ColumnFor(header.Column) is not { } column) return;

        SortBy(column);
        StatusLeft.Text = _sortColumn == SortColumn.None
            ? "Sorted by PKZ order (as stored)."
            : $"Sorted by {_sortColumn} {(_sortDescending ? "descending" : "ascending")}.";
    }

    /// <summary>
    /// Advance the sort state for one column and rebuild the view. Kept separate from the click handler
    /// so the headless check can drive the same transitions without synthesising a header.
    /// </summary>
    internal void SortBy(SortColumn column)
    {
        if (_sortColumn == column)
        {
            if (_sortDescending && column == SortColumn.Name)
            {
                // Name descending is the last state before returning to the archive's own order, so a
                // user can always get back to "as stored" without a separate control. Sorting is
                // view-only, so this never touches what a save writes.
                _sortColumn = SortColumn.None;
                _sortDescending = false;
            }
            else
            {
                _sortDescending = !_sortDescending;
            }
        }
        else
        {
            _sortColumn = column;
            _sortDescending = false;
        }

        ApplyFilterAndSort();
    }

    /// <summary>Which sortable column a grid column is, or null for one that is not sortable.</summary>
    private SortColumn? ColumnFor(GridViewColumn? column)
    {
        if (ReferenceEquals(column, ColumnName)) return SortColumn.Name;
        if (ReferenceEquals(column, ColumnSize)) return SortColumn.Size;
        if (ReferenceEquals(column, ColumnFormat)) return SortColumn.Format;
        if (ReferenceEquals(column, ColumnCompression)) return SortColumn.Compression;
        return null;
    }

    private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Shift+Up/Down extends the selection, which the ListView already does; this only makes sure the
        // grid keeps focus so it reaches the control rather than a menu.
        if (e.Key is Key.Up or Key.Down && Keyboard.Modifiers == ModifierKeys.Shift)
            FileList.Focus();
    }

    // --- expansion -------------------------------------------------------------------------

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click expands or collapses a folder and does nothing on a file.
        if (FileList.SelectedItem is not FileRow row || !row.IsFolder) return;
        ToggleExpanded(row);
        e.Handled = true;
    }

    private void OnChevronClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileRow row } && row.IsFolder)
        {
            ToggleExpanded(row);
            e.Handled = true;
        }
    }

    private void ToggleExpanded(FileRow row)
    {
        if (_expanded.Contains(row.Node.Path)) _expanded.Remove(row.Node.Path);
        else _expanded.Add(row.Node.Path);
        RebuildRows();
    }

    private void OnExpandAll(object sender, RoutedEventArgs e)
    {
        foreach (var folder in VfsTree.AllFolders(_workspace.Root))
            if (folder.Path.Length > 0 && !folder.IsEmptyFolder)
                _expanded.Add(folder.Path);
        RebuildRows();
    }

    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        _expanded.Clear();
        RebuildRows();
    }

    // --- search ----------------------------------------------------------------------------

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchClear.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyFilterAndSort();
    }

    private void OnSearchClear(object sender, MouseButtonEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    // --- status ----------------------------------------------------------------------------

    private void UpdateStatus()
    {
        bool modified = _workspace.IsModified;

        // The title carries the modified marker the brief asks for, alongside the loaded file names.
        string names = _workspace.Archives.Count == 0
            ? "no pkz loaded"
            : string.Join(", ", _workspace.Archives.Select(a => a.FileName));
        Title = $"PKZ Explorer \u2014 {names}{(modified ? " *" : string.Empty)}";

        StatusLeft.Text = _workspace.Archives.Count == 0
            ? "Open a pkz file to begin."
            : $"{_rows.Count} of {_workspace.AllFiles.Count()} entries shown \u2014 {_workspace.Archives.Count} pkz loaded";

        int staged = _workspace.ModifiedArchives.Count();
        int edits = _workspace.Archives.Sum(a => a.Edits.Count);
        StatusMiddle.Text = modified
            ? $"{edits} staged change(s) in {staged} archive(s) \u2014 not saved"
            : string.Empty;

        long total = _workspace.AllFiles.Sum(f => f.UncompressedSize);
        StatusRight.Text = _workspace.Archives.Count == 0 ? string.Empty : ByteSize.Format(total);
    }

    // --- long operations -------------------------------------------------------------------

    /// <summary>
    /// Run <paramref name="work"/> with the progress strip visible and Cancel wired up. Returns null
    /// when the user cancelled or the work failed.
    /// <para>
    /// The work reports through <see cref="ReportProgress"/>, which also pumps the dispatcher so the
    /// window keeps repainting and Cancel stays clickable for the whole run.
    /// </para>
    /// </summary>
    private T? RunWithProgress<T>(string title, Func<CancellationToken, T> work) where T : class
    {
        if (!TryBeginOperation(title)) return null;
        try
        {
            return work(_operation!.Token);
        }
        catch (OperationCanceledException)
        {
            StatusLeft.Text = "Cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// The same, for an operation whose result is a value type. A separate overload is needed because
    /// an unconstrained generic cannot express "null when cancelled" for a struct.
    /// </summary>
    private T? RunWithProgressStruct<T>(string title, Func<CancellationToken, T> work) where T : struct
    {
        if (!TryBeginOperation(title)) return null;
        try
        {
            return work(_operation!.Token);
        }
        catch (OperationCanceledException)
        {
            StatusLeft.Text = "Cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Run a long operation that produces no value; returns false when it was cancelled.</summary>
    private bool RunWithProgress(string title, Action<CancellationToken> work)
    {
        if (!TryBeginOperation(title)) return false;
        try
        {
            work(_operation!.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusLeft.Text = "Cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Show the progress strip and arm cancellation. False when one is already running.</summary>
    private bool TryBeginOperation(string title)
    {
        if (_operation is not null) return false;

        _operation = new CancellationTokenSource();
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressLabel.Text = title + "...";
        ProgressBar.Value = 0;
        Mouse.OverrideCursor = Cursors.Wait;
        UpdateLayout();
        return true;
    }

    private void EndOperation()
    {
        Mouse.OverrideCursor = null;
        ProgressPanel.Visibility = Visibility.Collapsed;
        _operation?.Dispose();
        _operation = null;
    }

    /// <summary>Report progress and give the UI a chance to repaint and to observe a Cancel click.</summary>
    private void ReportProgress(OperationProgress progress)
    {
        ProgressLabel.Text = progress.Message;
        ProgressBar.Value = progress.Fraction;

        // Pumping the dispatcher at the lowest priority is what keeps the window responsive during a
        // long save without re-entering the operation.
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        if (_operation?.IsCancellationRequested == true)
            throw new OperationCanceledException(_operation.Token);
    }

    private void OnCancelOperation(object sender, RoutedEventArgs e) => CancelOperation();

    private void CancelOperation() => _operation?.Cancel();

    // --- prompts ---------------------------------------------------------------------------

    /// <summary>
    /// Ask about staged, unsaved edits. Returns true when it is safe to continue (saved, discarded, or
    /// nothing staged), false when the user cancelled the whole action.
    /// <para>
    /// A headless run has nobody to answer the prompt, so it reports that it is not safe rather than
    /// blocking: the caller then abandons the action, which is the safe default.
    /// </para>
    /// </summary>
    private bool ConfirmDiscardStagedEdits(string action)
    {
        if (!_workspace.IsModified) return true;
        if (_headless) return false;

        int edits = _workspace.Archives.Sum(a => a.Edits.Count);
        var answer = MessageBox.Show(this,
            $"There {(edits == 1 ? "is 1 staged change" : $"are {edits} staged changes")} that have not been saved.\n\n" +
            $"Save before {action}?",
            "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        return answer switch
        {
            MessageBoxResult.Yes => SaveArchives(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    // --- shortcuts -------------------------------------------------------------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && e.Key == Key.O) { OnMenuOpen(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.S) { OnMenuSaveAs(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.S) { OnMenuSave(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.F) { FocusSearch(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.A)
        {
            // Ctrl+A selects every visible row; it must not steal focus from the search box, where it
            // means "select all text".
            if (!SearchBox.IsKeyboardFocusWithin) { FileList.SelectAll(); e.Handled = true; }
            return;
        }

        // Delete and F2 act on the list, so they are only handled when the grid has focus.
        if (!FileList.IsKeyboardFocusWithin) { base.OnPreviewKeyDown(e); return; }

        if (e.Key == Key.Delete) { OnDelete(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.F2) { OnRename(this, new RoutedEventArgs()); e.Handled = true; }

        base.OnPreviewKeyDown(e);
    }

    private void OnOpenMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // --- headless UI checks -----------------------------------------------------------------

    /// <summary>
    /// Mark this window as a headless check, so nothing it does can raise a modal prompt. Called before
    /// any check runs, because a failing check must not be able to block on a dialog.
    /// </summary>
    internal void UiTestHeadless() => _headless = true;

    /// <summary>
    /// Load an archive without any dialog or confirmation. Used by the <c>--uitest</c> smoke check, so
    /// the load path it exercises is the same one the File menu uses.
    /// </summary>
    internal void UiTestLoad(string path) => UiTestLoadMany(new[] { path });
    /// <summary>The same for several archives, which is what <c>--show</c> uses to preload a session.</summary>
    internal void UiTestLoadMany(IReadOnlyList<string> paths)
    {
        _workspace.Load(paths, _settings.ResolveOodleDll());
        _expanded.Clear();

        // Open the top-level folders so a preloaded session shows real content rather than a single
        // collapsed row per archive. A top-level folder has depth 0 (it contains no separator), which is
        // why the test is for zero rather than one.
        foreach (var folder in VfsTree.AllFolders(_workspace.Root))
            if (VirtualPath.Depth(folder.Path) == 0 && folder.Path.Length > 0 && !folder.IsEmptyFolder)
                _expanded.Add(folder.Path);

        RebuildRows();
        UpdateStatus();
    }

    /// <summary>
    /// Leave an edit staged, so the check that follows can prove closing the window does not raise a
    /// prompt in a headless run.
    /// </summary>
    internal void UiTestStageSomething()
    {
        var row = _workspace.AllFiles.FirstOrDefault(f => f.Kind == VfsNodeKind.File);
        if (row is not null) _workspace.StageDelete(new[] { row });
    }

    /// <summary>A one-line summary of what the list is showing, for the smoke check to assert on.</summary>
    internal string UiTestState() =>
        $"{_rows.Count} of {_workspace.AllFiles.Count()} rows, " +
        $"{_workspace.Archives.Count} archive(s), title '{Title}'";

    /// <summary>
    /// Drive the parts of the shell that only exist in response to input — expansion, the search filter,
    /// every sort column, and the staged-edit marker — and report each result. These cannot be checked
    /// by constructing the window, and a screenshot cannot show them either.
    /// </summary>
    internal IEnumerable<(string Name, string Result)> UiTestInteractive()
    {
        // Start from a known state: whatever the caller left collapsed or expanded, the checks below
        // compare against a baseline this method establishes itself.
        OnCollapseAll(this, new RoutedEventArgs());
        int collapsed = _rows.Count;

        // Expansion: Expand all must reveal every file, then Collapse all must return to the baseline.
        OnExpandAll(this, new RoutedEventArgs());
        int expanded = _rows.Count;
        yield return ("expand all", expanded > collapsed
            ? $"{collapsed} -> {expanded} rows"
            : $"FAIL did not expand ({collapsed} -> {expanded})");

        // Every file must be on screen once everything is expanded. The comparison is against the file
        // count rather than the row count, because the rows also include the folder that holds them.
        int visibleFiles = _rows.Count(r => !r.IsFolder);
        int totalFiles = _workspace.AllFiles.Count();
        yield return ("expand all shows every file", visibleFiles == totalFiles
            ? $"{visibleFiles} file rows"
            : $"FAIL {visibleFiles} of {totalFiles} file rows are visible");

        OnCollapseAll(this, new RoutedEventArgs());
        yield return ("collapse all", _rows.Count == collapsed
            ? $"{_rows.Count} rows"
            : $"FAIL expected {collapsed} rows, got {_rows.Count}");

        // Search: a term taken from a real path must match, and a nonsense term must match nothing.
        OnExpandAll(this, new RoutedEventArgs());
        var sample = _workspace.AllFiles.FirstOrDefault();
        if (sample is not null)
        {
            string leaf = VirtualPath.GetName(sample.Path);
            SearchBox.Text = leaf;
            int matched = _rows.Count;
            yield return ("search by name", matched > 0
                ? $"'{leaf}' matched {matched} row(s)"
                : $"FAIL '{leaf}' matched nothing");

            SearchBox.Text = "zzz-no-such-entry-zzz";
            yield return ("search with no match", _rows.Count == 0
                ? "0 rows, empty state shown"
                : $"FAIL expected 0 rows, got {_rows.Count}");

            SearchBox.Clear();
        }

        // Sorting: each column must reorder without losing or duplicating a row.
        int baseline = _rows.Count;
        foreach (var (column, label) in new[]
                 {
                     (SortColumn.Name, "Filename"),
                     (SortColumn.Size, "Size"),
                     (SortColumn.Format, "Format"),
                     (SortColumn.Compression, "Compression"),
                 })
        {
            SortBy(column);
            int after = _rows.Count;
            yield return ($"sort by {label}", after == baseline
                ? $"{after} rows, header '{ColumnName.Header}'"
                : $"FAIL row count changed {baseline} -> {after}");
        }

        // Sorting is view-only: the archive's stored order must be untouched.
        var storedFirst = _workspace.AllFiles.First().Path;
        SortBy(SortColumn.None);
        yield return ("sort does not reorder the archive",
            VirtualPath.CompareEqual(_workspace.AllFiles.First().Path, storedFirst)
                ? $"stored order still starts with {storedFirst}"
                : $"FAIL stored order changed to {_workspace.AllFiles.First().Path}");

        // Staged edits: delete one file and confirm the marker, the title and the row count follow.
        var doomed = _workspace.AllFiles.FirstOrDefault(f => f.Kind == VfsNodeKind.File);
        if (doomed is not null)
        {
            int before = _workspace.AllFiles.Count();
            _workspace.StageDelete(new[] { doomed });
            int after = _workspace.AllFiles.Count();
            yield return ("staged delete", after == before - 1 && Title.Contains('*')
                ? $"{before} -> {after} entries, title marked modified"
                : $"FAIL {before} -> {after} entries, title '{Title}'");

            _workspace.DiscardEdits();
            yield return ("discard restores", _workspace.AllFiles.Count() == before && !Title.Contains('*')
                ? $"{before} entries restored, title clean"
                : $"FAIL {_workspace.AllFiles.Count()} entries, title '{Title}'");
        }
    }
}
