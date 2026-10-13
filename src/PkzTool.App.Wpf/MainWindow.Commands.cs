using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PkzTool.App.Wpf.Dialogs;
using PkzTool.Core;
using PkzTool.Core.Archives;
using PkzTool.Core.Compression;
using PkzTool.Core.Operations;
using PkzTool.Core.Vfs;
using PkzTool.App.Wpf.Theming;
using PkzTool.Core.Workspace;
using Xt1.Core.Util;

namespace PkzTool.App.Wpf;

/// <summary>
/// The window's commands: the File menu, the extract/replace/recompress/add/delete/rename actions,
/// drag and drop, and the dialogs they open. Split from <c>MainWindow.xaml.cs</c> so the shell setup
/// and the row model stay readable next to each other.
/// </summary>
public partial class MainWindow
{
    private const string PkzFilter = "Bayonetta 3 pkz (*.pkz)|*.pkz|All files (*.*)|*.*";

    // --- File menu --------------------------------------------------------------------------

    private void OnFileMenuOpened(object sender, RoutedEventArgs e) => BuildRecentMenu();

    /// <summary>
    /// Rebuild Open Recent from the settings each time the menu opens, so a file deleted since the last
    /// launch is not offered.
    /// </summary>
    private void BuildRecentMenu()
    {
        _settings.PruneRecent();
        MenuOpenRecent.Items.Clear();

        var recent = _settings.RecentFiles ?? new List<string>();
        if (recent.Count == 0)
        {
            MenuOpenRecent.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }

        foreach (var path in recent)
        {
            // Each entry is a single pkz file, which is what the brief asks for.
            var item = new MenuItem
            {
                Header = Path.GetFileName(path),
                ToolTip = path,
                InputGestureText = Path.GetDirectoryName(path),
            };
            item.Click += (_, _) => LoadArchives(new[] { path });
            MenuOpenRecent.Items.Add(item);
        }

        MenuOpenRecent.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        var clear = new MenuItem { Header = "Clear the list" };
        clear.Click += (_, _) =>
        {
            _settings.RecentFiles = new List<string>();
            _settings.Save();
        };
        MenuOpenRecent.Items.Add(clear);
    }

    private void OnMenuOpen(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open pkz files",
            Filter = PkzFilter,
            Multiselect = true,
            InitialDirectory = _settings.LastDirectory,
        };
        if (dialog.ShowDialog(this) != true) return;

        // Opening replaces everything currently loaded; there is no way to add to the view.
        LoadArchives(dialog.FileNames);
    }

    private void OnMenuSave(object sender, RoutedEventArgs e) => SaveArchives();

    /// <summary>Write every modified archive. Returns false when something failed or was cancelled.</summary>
    private bool SaveArchives()
    {
        if (!_workspace.IsModified)
        {
            StatusLeft.Text = "Nothing to save.";
            return true;
        }

        var outcome = RunWithProgress("Saving", token =>
            _workspace.Save(ReportProgress, token));

        if (outcome is null) return false;

        if (outcome.Failures.Count > 0)
        {
            MessageBox.Show(this,
                "Some archives could not be written:\n\n" +
                string.Join("\n\n", outcome.Failures.Select(f => $"{Path.GetFileName(f.Path)}\n{f.Error}")),
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        StatusLeft.Text = outcome.Written > 0
            ? $"Saved {outcome.Written} archive(s)."
            : "No archive was written.";

        UpdateStatus();
        return outcome.Failures.Count == 0;
    }

    private void OnMenuSaveAs(object sender, RoutedEventArgs e)
    {
        if (_workspace.Archives.Count == 0) return;

        // A save dialog per loaded pkz, as the brief specifies.
        var targets = new Dictionary<LoadedArchive, string>();
        foreach (var archive in _workspace.Archives)
        {
            var dialog = new SaveFileDialog
            {
                Title = $"Save {archive.FileName} as",
                Filter = PkzFilter,
                FileName = archive.FileName,
                InitialDirectory = _settings.LastDirectory,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != true)
            {
                StatusLeft.Text = "Save As cancelled; nothing was written.";
                return;
            }

            targets[archive] = dialog.FileName;
            _settings.LastDirectory = Path.GetDirectoryName(dialog.FileName);
        }

        var outcome = RunWithProgress("Saving", token => _workspace.SaveAs(targets, ReportProgress, token));
        if (outcome is null) return;

        if (outcome.Failures.Count > 0)
        {
            MessageBox.Show(this,
                "Some archives could not be written:\n\n" +
                string.Join("\n\n", outcome.Failures.Select(f => $"{Path.GetFileName(f.Path)}\n{f.Error}")),
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        StatusLeft.Text = $"Saved {outcome.Written} archive(s).";
        UpdateStatus();
    }

    private void OnMenuCloseAll(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardStagedEdits("closing the view")) return;
        _workspace.Clear();
        _expanded.Clear();
        RebuildRows();
        UpdateStatus();
    }

    private void OnMenuExit(object sender, RoutedEventArgs e) => Close();

    // --- PKZ from folder ---------------------------------------------------------------------

    private void OnMenuFromFolder(object sender, RoutedEventArgs e)
    {
        var folderDialog = new OpenFolderDialog
        {
            Title = "Choose the folder to pack",
            InitialDirectory = _settings.LastSourceFolder ?? _settings.LastDirectory,
        };
        if (folderDialog.ShowDialog(this) != true) return;

        // The compression type is picked once for the whole run, before the save dialog.
        var compression = CompressionDialog.Ask(this, "Pack folder as",
            _settings.DefaultCompression, _settings.DefaultLevel);
        if (compression is null) return;

        var saveDialog = new SaveFileDialog
        {
            Title = "Save the new pkz",
            Filter = PkzFilter,
            FileName = PkzPath.DefaultNameFor(folderDialog.FolderName),
            InitialDirectory = Path.GetDirectoryName(folderDialog.FolderName),
        };
        if (saveDialog.ShowDialog(this) != true) return;

        _settings.DefaultCompression = compression.Value.Compression;
        _settings.DefaultLevel = compression.Value.Level;
        _settings.LastSourceFolder = folderDialog.FolderName;

        string? oodle = EnsureOodleAvailable(compression.Value.Compression);
        if (compression.Value.Compression == EntryCompression.OodleKraken && oodle is null) return;

        var result = RunWithProgressStruct("Packing folder", token => PkzFolderBuilder.Build(
            folderDialog.FolderName, saveDialog.FileName,
            compression.Value.Compression, compression.Value.Level, oodle,
            forceCompression: false, ReportProgress, token));

        if (result is null) return;

        var build = result.Value;
        if (build.Errors.Count > 0)
            MessageBox.Show(this,
                $"{build.Errors.Count} file(s) could not be read:\n\n" +
                string.Join("\n", build.Errors.Take(12).Select(e => e.Path)),
                "Some files were skipped", MessageBoxButton.OK, MessageBoxImage.Warning);

        // Afterwards the new pkz is loaded, replacing the view.
        LoadArchives(new[] { saveDialog.FileName });

        StatusLeft.Text =
            $"Packed {build.FileCount} file(s) into {Path.GetFileName(saveDialog.FileName)} " +
            $"({ByteSize.Format(build.TotalBytes)} \u2192 {ByteSize.Format(build.ArchiveBytes)}).";
    }

    // --- extract ------------------------------------------------------------------------------

    private void OnExtractSelected(object sender, RoutedEventArgs e)
    {
        var files = SelectedFiles();
        if (files.Count == 0) return;
        Extract(files);
    }

    private void OnExtractAll(object sender, RoutedEventArgs e)
    {
        var all = _workspace.AllFiles.ToList();
        if (all.Count == 0) return;
        Extract(all);
    }

    /// <summary>
    /// Extract files to a folder, asking overwrite / skip / cancel for anything already on disk. The
    /// question is asked once for the whole run rather than per file, which is what makes extracting
    /// over a large existing tree bearable.
    /// </summary>
    private void Extract(List<VfsNode> files)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the destination folder",
            InitialDirectory = _settings.LastDirectory,
        };
        if (dialog.ShowDialog(this) != true) return;

        _settings.LastDirectory = dialog.FolderName;

        // The overwrite question is asked once for the run, which is what makes extracting over an
        // existing tree bearable.
        OverwriteAnswer choice = OverwriteAnswer.Overwrite;
        int existing = files.Count(f =>
        {
            string? target = ExtractionService.ResolveWithin(dialog.FolderName, f.Path);
            return target is not null && File.Exists(target);
        });

        if (existing > 0)
        {
            var answer = OverwriteDialog.Ask(this, existing);
            if (answer is null) return;
            choice = answer.Value;
        }

        // The extraction worker runs off the UI thread, so the prompt is already resolved into a
        // constant decision here.
        OverwriteAction Decide(string _) => choice == OverwriteAnswer.Overwrite
            ? OverwriteAction.Overwrite
            : OverwriteAction.Skip;

        var summary = RunWithProgressStruct("Extracting", token => ExtractionService.Extract(
            _workspace, files, dialog.FolderName, Decide, ReportProgress, token));

        if (summary is null) return;

        var result = summary.Value;
        if (result.Errors.Count > 0)
            MessageBox.Show(this,
                $"{result.Errors.Count} file(s) could not be written:\n\n" +
                string.Join("\n", result.Errors.Take(12).Select(e => $"{e.Path}: {e.Error}")),
                "Some files failed", MessageBoxButton.OK, MessageBoxImage.Warning);

        StatusLeft.Text = $"{result.Describe()} to {dialog.FolderName}";
    }

    // --- replace ------------------------------------------------------------------------------

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        var selection = SelectedNodes();
        // Replace is single-selection only and disabled on folders.
        if (selection.Count != 1 || selection[0].Kind != VfsNodeKind.File) return;

        var node = selection[0];
        var compression = CompressionDialog.Ask(this, $"Replace {node.DisplayName}",
            node.Compression, CompressionCodec.DefaultLevel(node.Compression));
        if (compression is null) return;

        string? oodle = EnsureOodleAvailable(compression.Value.Compression);
        if (compression.Value.Compression == EntryCompression.OodleKraken && oodle is null) return;

        var dialog = new OpenFileDialog
        {
            Title = $"Choose the new content for {node.Path}",
            Filter = "All files (*.*)|*.*",
            InitialDirectory = _settings.LastDirectory,
        };
        if (dialog.ShowDialog(this) != true) return;

        _settings.LastDirectory = Path.GetDirectoryName(dialog.FileName);
        _settings.DefaultCompression = compression.Value.Compression;
        _settings.DefaultLevel = compression.Value.Level;

        try
        {
            byte[] data = File.ReadAllBytes(dialog.FileName);
            var archive = _workspace.Archives.FirstOrDefault(a => ReferenceEquals(a.Archive, node.Owner));
            if (archive is null) return;

            _workspace.OodlePath = oodle;
            _workspace.StageAddOrReplace(archive, node.Path, data,
                compression.Value.Compression, compression.Value.Level,
                allowOverwrite: true, replaceKey: node.Key);

            StatusLeft.Text = $"Staged a replacement for {node.Path} ({ByteSize.Format(data.Length)}).";
            Reselect(node.Key);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Replace failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- recompress ---------------------------------------------------------------------------

    private void OnRecompress(object sender, RoutedEventArgs e)
    {
        var files = SelectedFiles();
        if (files.Count == 0) return;

        // One dialog picks the type for everything selected.
        var compression = CompressionDialog.Ask(this, $"Recompress {files.Count} file(s)",
            _settings.DefaultCompression, _settings.DefaultLevel);
        if (compression is null) return;

        string? oodle = EnsureOodleAvailable(compression.Value.Compression);
        if (compression.Value.Compression == EntryCompression.OodleKraken && oodle is null) return;

        _settings.DefaultCompression = compression.Value.Compression;
        _settings.DefaultLevel = compression.Value.Level;
        _workspace.OodlePath = oodle;

        foreach (var file in files)
            _workspace.StageRecompress(file, compression.Value.Compression, compression.Value.Level);

        StatusLeft.Text =
            $"Staged recompression of {files.Count} file(s) to " +
            $"{CompressionCodec.DisplayName(compression.Value.Compression)}.";
    }

    // --- add ----------------------------------------------------------------------------------

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_workspace.Archives.Count == 0) return;

        // The target folder follows the selection: a selected folder, else the selected file's folder,
        // else the root.
        var target = _workspace.AddTargetFolder(SelectedNodes());

        // First dialog: which loaded pkz.
        var pkz = PkzPickDialog.Ask(this, _workspace.Archives, target.Path);
        if (pkz is null) return;

        // Second dialog: the compression type.
        var compression = CompressionDialog.Ask(this, "Add files as",
            _settings.DefaultCompression, _settings.DefaultLevel);
        if (compression is null) return;

        string? oodle = EnsureOodleAvailable(compression.Value.Compression);
        if (compression.Value.Compression == EntryCompression.OodleKraken && oodle is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Choose files to add",
            Filter = "All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = _settings.LastDirectory,
        };
        if (dialog.ShowDialog(this) != true) return;

        _settings.DefaultCompression = compression.Value.Compression;
        _settings.DefaultLevel = compression.Value.Level;
        _settings.LastDirectory = Path.GetDirectoryName(dialog.FileNames[0]);

        AddFiles(pkz, target.Path, dialog.FileNames,
            compression.Value.Compression, compression.Value.Level, oodle);
    }

    /// <summary>
    /// Stage a set of loose files into a folder of one archive.
    /// <para>
    /// Collisions are resolved once for the whole run rather than per file: adding fifty files over
    /// existing names would otherwise raise fifty modal prompts. This matches how Extract asks, and is
    /// the only way a bulk add is usable.
    /// </para>
    /// </summary>
    private void AddFiles(LoadedArchive archive, string folder, IReadOnlyList<string> files,
        EntryCompression compression, int level, string? oodle)
    {
        _workspace.OodlePath = oodle;

        // Work out the collisions up front so the question can be asked once with a real count.
        var planned = new List<(string File, string Path)>();
        var colliding = new List<(string File, string Path)>();
        foreach (var file in files)
        {
            string path = VirtualPath.Combine(folder, Path.GetFileName(file));
            if (_workspace.WouldCollide(archive, path)) colliding.Add((file, path));
            else planned.Add((file, path));
        }

        // A colliding file is either overwritten or left alone, for every collision in this run.
        bool overwrite = true;
        if (colliding.Count > 0)
        {
            var answer = MessageBox.Show(this,
                colliding.Count == 1
                    ? $"1 file already exists in {archive.FileName}:\n\n{colliding[0].Path}\n\nOverwrite it?"
                    : $"{colliding.Count} files already exist in {archive.FileName}.\n\n" +
                      $"Overwrite them? Choosing No adds only the {planned.Count} new file(s).",
                "Name collision", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
            {
                StatusLeft.Text = "Add cancelled.";
                return;
            }
            overwrite = answer == MessageBoxResult.Yes;
            if (overwrite) planned.AddRange(colliding);
        }

        int staged = 0;
        var failed = new List<string>();
        foreach (var (file, path) in planned)
        {
            try
            {
                byte[] data = File.ReadAllBytes(file);
                _workspace.StageAddOrReplace(archive, path, data, compression, level,
                    allowOverwrite: true);
                staged++;
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        string where = folder.Length == 0 ? "(root)" : folder;
        StatusLeft.Text = staged == 0
            ? $"Nothing was added to {where} of {archive.FileName}."
            : $"Staged {staged} file(s) into {where} of {archive.FileName}" +
              (colliding.Count > 0 && !overwrite ? $"; {colliding.Count} existing file(s) left alone" : string.Empty) +
              ".";

        if (failed.Count > 0)
            MessageBox.Show(this,
                $"{failed.Count} file(s) could not be read:\n\n" + string.Join("\n", failed.Take(12)),
                "Some files were skipped", MessageBoxButton.OK, MessageBoxImage.Warning);

        if (staged > 0 && _rows.Count > 0) SortAndKeepSelection();
    }

    // --- revert -------------------------------------------------------------------------------

    /// <summary>
    /// Undo every staged change to the selected rows, leaving other rows' changes alone. This is the
    /// per-row counterpart to "Close all" discarding everything, and the only way back from a delete
    /// without losing unrelated work.
    /// </summary>
    private void OnRevert(object sender, RoutedEventArgs e)
    {
        var selection = SelectedNodes();
        if (selection.Count == 0) return;

        int staged = selection.SelectMany(n => n.DescendantFiles()).Count(f => f.IsModified);
        if (staged == 0)
        {
            StatusLeft.Text = "Nothing to revert in the selection.";
            return;
        }

        _workspace.RevertEdits(selection);
        StatusLeft.Text = $"Reverted {staged} staged change(s).";
    }

    // --- delete -------------------------------------------------------------------------------

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var selection = SelectedNodes();
        if (selection.Count == 0) return;

        var files = SelectedFiles();
        int folders = selection.Count(n => n.Kind == VfsNodeKind.Folder);

        // Delete always asks for confirmation, and a folder's confirmation says what it will take.
        string what = selection.Count == 1
            ? folders == 1
                ? $"the folder '{selection[0].Path}' and the {files.Count} file(s) under it"
                : $"'{selection[0].Path}'"
            : $"{files.Count} file(s){(folders > 0 ? $" (including {folders} folder(s))" : string.Empty)}";

        var answer = MessageBox.Show(this, $"Delete {what}?\n\nThe change is staged until you save.",
            "Confirm delete", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        int removed = _workspace.StageDelete(selection);
        StatusLeft.Text = $"Staged deletion of {removed} file(s).";
    }

    // --- rename -------------------------------------------------------------------------------

    private void OnRename(object sender, RoutedEventArgs e)
    {
        var selection = SelectedNodes();
        if (selection.Count != 1) return;

        var node = selection[0];
        bool isFolder = node.Kind == VfsNodeKind.Folder;
        var current = node.LeafName;

        var prompt = new TextPromptDialog(
            isFolder ? "Rename folder" : "Rename file",
            isFolder
                ? "This renames the path segment for every file under the folder."
                : "This changes the file name only; the folder stays the same.",
            current) { Owner = this };
        if (prompt.ShowDialog() != true) return;

        string newName = prompt.Value;
        if (VirtualPath.CompareEqual(newName, current)) return;

        if (VirtualPath.ValidateSegment(newName) is { } error)
        {
            MessageBox.Show(this, error, "Invalid name", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // A rename onto an existing name in that folder of that pkz asks overwrite or cancel.
        var owner = _workspace.Archives.FirstOrDefault(a => ReferenceEquals(a.Archive, node.Owner));
        string newPath = VirtualPath.WithName(node.Path, newName);
        if (owner is not null && _workspace.WouldCollide(owner, newPath, node.Key))
        {
            var answer = MessageBox.Show(this,
                $"'{newPath}' already exists in {owner.FileName}.\n\nOverwrite it?",
                "Name collision", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            // Overwriting means the existing entry goes first, so the archive does not end up with two
            // entries at the same path.
            var clash = _workspace.AllFiles.FirstOrDefault(f =>
                ReferenceEquals(f.Owner, node.Owner) &&
                VirtualPath.Comparer.Equals(f.Path, newPath));
            if (clash is not null) _workspace.StageDelete(new[] { clash });
        }

        try
        {
            int affected = _workspace.StageRename(node, newName);
            StatusLeft.Text = affected > 1
                ? $"Staged rename of '{node.Path}' \u2192 '{newPath}' ({affected} files)."
                : $"Staged rename of '{node.Path}' \u2192 '{newPath}'.";
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid name", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- settings and about -------------------------------------------------------------------

    private void OnMenuSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _settings = dialog.Result;
        _settings.Save();

        if (dialog.OodleChanged)
        {
            _workspace.OodlePath = _settings.ResolveOodleDll();
            StatusLeft.Text = _settings.OodleDllPath is { Length: > 0 }
                ? $"Oodle dll: {_settings.OodleDllPath}"
                : "Oodle dll: auto-detect";
        }

        UpdateCommandStates();    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        _settings.Theme = ThemeManager.IsDark ? Xt1.Core.Settings.AppTheme.Light : Xt1.Core.Settings.AppTheme.Dark;
        ThemeManager.Set(_settings.Theme);
        ThemeIcon.Text = ThemeManager.IsDark ? "\uE706" : "\uE708";
        _settings.Save();
    }

    private void OnMenuAbout(object sender, RoutedEventArgs e)
    {
        var about = new AboutDialog(_workspace.Archives.Count, _workspace.AllFiles.Count(), _settings) { Owner = this };
        about.ShowDialog();
    }

    // --- Oodle --------------------------------------------------------------------------------

    /// <summary>
    /// Make sure a usable Oodle dll is known when a compression needs one, prompting for it the first
    /// time. Returns the resolved path, or null when the user declined or nothing usable was chosen.
    /// </summary>
    private string? EnsureOodleAvailable(EntryCompression compression)
    {
        if (compression != EntryCompression.OodleKraken) return _workspace.OodlePath;

        var resolved = _settings.ResolveOodleDll();
        if (resolved is null || !PkzTool.Core.Compression.Oodle.TryValidate(resolved, out _))
        {
            var dialog = new OodleDialog(_settings.OodleDllPath) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                StatusLeft.Text = "Oodle compression cancelled: no dll configured.";
                return null;
            }

            _settings.OodleDllPath = dialog.Result;
            _settings.Save();
            resolved = dialog.Result;
        }

        _workspace.OodlePath = resolved;
        return resolved;
    }

    // --- drag and drop ------------------------------------------------------------------------

    private Point _dragStart;
    private FileRow? _dragCandidate;

    private void OnListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(FileList);
        var item = ItemUnder(e.OriginalSource as DependencyObject);
        // Only file rows can be dragged out; a folder means "drop into me", which is handled by Drop.
        _dragCandidate = item is { IsFolder: false } ? item : null;
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e) => _dragCandidate = null;

    /// <summary>
    /// Begin a drag of the selected file rows. Dragging out extracts only when the drop lands on
    /// Explorer; dragging within the tree moves files between folders of the same pkz.
    /// </summary>
    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null) return;

        var current = e.GetPosition(FileList);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var files = SelectedFiles();
        if (files.Count == 0) return;

        // The payload carries both the virtual paths and, when every file shares one archive, that
        // archive, so a drop inside the tree can tell a legal move from an illegal one.
        var data = new DataObject();
        data.SetData(FileDropData.Format, files.Select(f => new FileDropData.Entry(f.Key, f.Path)).ToArray());

        // A FileDrop list is what lets Explorer accept the drop as an extract. The files are staged into
        // a per-gesture temp folder, which is removed as soon as the drag finishes: a drag-out of a few
        // large entries would otherwise leave hundreds of megabytes behind for the rest of the session.
        var staging = Path.Combine(Path.GetTempPath(), "PkzExplorerDrag-" + Guid.NewGuid().ToString("N"));
        var tempPaths = new System.Collections.Specialized.StringCollection();
        try
        {
            foreach (var file in files)
            {
                string? staged = StageForDragOut(file, staging);
                if (staged is not null) tempPaths.Add(staged);
            }
            if (tempPaths.Count > 0) data.SetFileDropList(tempPaths);

            _dragCandidate = null;
            // DoDragDrop blocks until the drop is complete, so Explorer has finished copying by the time
            // this returns and the staged copies can safely go.
            DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            RemoveStagingFolder(staging);
        }
    }

    /// <summary>Delete a drag-out staging folder, ignoring a failure to remove it.</summary>
    private static void RemoveStagingFolder(string staging)
    {
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catch (IOException)
        {
            // Explorer may still hold a handle for a moment; the folder is in temp and named uniquely,
            // so a leftover copy is harmless and never reused.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Drag feedback for a drop anywhere on the window. Files from Explorer are always accepted, because
    /// that is how a pkz is opened or added; an internal move is only legal over the list, which handles
    /// it itself.
    /// </summary>
    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    /// <summary>
    /// A drop anywhere on the window that the list did not already handle.
    /// <para>
    /// This exists because the list is not always there to accept the drop: with no archive loaded it is
    /// Collapsed, and the empty state on top of it is not hit-testable, so dropping a pkz onto a freshly
    /// opened window did nothing. The list keeps its own handler for internal moves, which need to know
    /// which folder the pointer was over.
    /// </para>
    /// </summary>
    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        e.Handled = true;
        DropFromExplorer(paths);
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        // From Explorer: a file drop is an add. From within the tree: a move onto a folder of the same
        // pkz is allowed, anything else is not.
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; return; }

        var target = ItemUnder(e.OriginalSource as DependencyObject);
        bool legal = target is { IsFolder: true } && CanMoveInto(target.Node);
        e.Effects = legal ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        // Files dragged in from Explorer follow exactly the same rules as Add.
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
            DropFromExplorer(paths);
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(FileDropData.Format) is not FileDropData.Entry[] entries || entries.Length == 0) return;

        var target = ItemUnder(e.OriginalSource as DependencyObject);
        // Dropping onto another pkz's folder does nothing.
        if (target is not { IsFolder: true }) return;
        if (!CanMoveInto(target.Node)) return;

        MoveWithinArchive(entries, target.Node);
        e.Handled = true;
    }

    /// <summary>
    /// Add files dropped from Explorer. The target folder and pkz are chosen the same way the Add
    /// command chooses them, and the compression type is asked for once.
    /// </summary>
    /// <summary>
    /// Handle files dropped onto the window from Explorer.
    /// <para>
    /// A dropped pkz is <em>opened</em>, which is what dropping an archive onto the window means and the
    /// only sensible reading when nothing is loaded yet. Anything else is loose content, and is added to
    /// the load's target folder, which needs an archive to add into.
    /// </para>
    /// </summary>
    private void DropFromExplorer(string[] paths)
    {
        // Split the drop by what it means: archives to open, everything else to add.
        var archives = paths.Where(p => p.EndsWith(".pkz", StringComparison.OrdinalIgnoreCase)
                                        && File.Exists(p)).ToList();
        var content = paths.Except(archives).ToList();

        if (archives.Count > 0)
        {
            // Opening replaces the view, so it asks about staged work first, exactly as File > Open does.
            // A cancelled prompt means the user declined the whole action, so nothing else in this drop
            // may proceed: the loose files would otherwise be added to the archives they just chose not
            // to replace.
            if (!LoadArchives(archives)) return;
        }

        if (content.Count == 0) return;

        // Loose files need somewhere to go, so they need an archive already loaded. With none, this is
        // reported rather than silently ignored, which is what it used to do.
        if (_workspace.Archives.Count == 0)
        {
            StatusLeft.Text = "Open a pkz first, then drop files onto it to add them.";
            return;
        }

        var target = _workspace.AddTargetFolder(SelectedNodes());
        var pkz = PkzPickDialog.Ask(this, _workspace.Archives, target.Path);
        if (pkz is null) return;

        var compression = CompressionDialog.Ask(this, "Add dropped files as",
            _settings.DefaultCompression, _settings.DefaultLevel);
        if (compression is null) return;

        string? oodle = EnsureOodleAvailable(compression.Value.Compression);
        if (compression.Value.Compression == EntryCompression.OodleKraken && oodle is null) return;

        // A dropped folder contributes its contents, matching Add; a bare file keeps its name.
        var files = new List<string>();
        foreach (var path in content)
        {
            if (Directory.Exists(path))
                files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));
            else if (File.Exists(path))
                files.Add(path);
        }

        if (files.Count == 0) return;

        _settings.DefaultCompression = compression.Value.Compression;
        _settings.DefaultLevel = compression.Value.Level;
        AddFiles(pkz, target.Path, files, compression.Value.Compression, compression.Value.Level, oodle);
    }

    /// <summary>
    /// Move files into another folder, within the same pkz only. A move is staged as a rename of the
    /// path, which is exactly what it is inside an archive.
    /// </summary>
    private void MoveWithinArchive(FileDropData.Entry[] entries, VfsNode targetFolder)
    {
        int moved = 0;
        foreach (var entry in entries)
        {
            var node = _workspace.FindByKey(entry.Key);
            if (node is null || node.Kind != VfsNodeKind.File) continue;

            var owner = _workspace.Archives.FirstOrDefault(a => ReferenceEquals(a.Archive, node.Owner));
            if (owner is null) continue;

            // Same-pkz only: a drop onto another archive's folder does nothing.
            if (!ReferenceEquals(owner, ArchiveOfFolder(targetFolder))) continue;

            string newPath = VirtualPath.Combine(targetFolder.Path, node.LeafName);
            if (VirtualPath.Comparer.Equals(newPath, node.Path)) continue;

            if (_workspace.WouldCollide(owner, newPath, node.Key))
            {
                var answer = MessageBox.Show(this,
                    $"'{newPath}' already exists in {owner.FileName}.\n\nOverwrite it?",
                    "Name collision", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK) continue;

                var clash = _workspace.AllFiles.FirstOrDefault(f =>
                    ReferenceEquals(f.Owner, node.Owner) &&
                    VirtualPath.Comparer.Equals(f.Path, newPath));
                if (clash is not null) _workspace.StageDelete(new[] { clash });
            }

            // A move keeps the leaf name and changes only the folder, which inside one archive is a
            // rename of the whole path.
            _workspace.StageMove(node, newPath);
            moved++;
        }

        StatusLeft.Text = moved > 0
            ? $"Staged a move of {moved} file(s) into '{targetFolder.Path}'."
            : "Nothing was moved.";
    }

    /// <summary>
    /// The archive a folder belongs to, taken from any file under it. A folder that holds nothing is
    /// not a legal move target, which <see cref="CanMoveInto"/> already rejects.
    /// </summary>
    private PkzArchive? ArchiveOfFolder(VfsNode folder) =>
        folder.DescendantFiles().FirstOrDefault()?.Owner;

    /// <summary>
    /// True when a move into this folder is legal: it must exist in exactly one archive, because a
    /// drop has no way to say which archive it means.
    /// </summary>
    private bool CanMoveInto(VfsNode folder)
    {
        var owners = folder.DescendantFiles().Select(f => f.Owner).Distinct().ToList();
        return owners.Count == 1 && owners[0] is not null;
    }

    /// <summary>
    /// Write one file to a temp folder so a drag onto Explorer has real content to copy. Returns the
    /// temp path, or null when the file could not be written.
    /// </summary>
    /// <summary>
    /// Write one file to the drag's staging folder so a drag onto Explorer has real content to copy.
    /// Returns the staged path, or null when the file could not be written.
    /// </summary>
    private string? StageForDragOut(VfsNode file, string staging)
    {
        try
        {
            string? target = ExtractionService.ResolveWithin(staging, file.Path);
            if (target is null) return null;

            string? directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(target, _workspace.ReadData(file));
            return target;
        }
        catch (Exception)
        {
            // A drag-out that cannot be prepared simply contributes no file; the internal drag still
            // works, so this must not surface as a modal error mid-gesture.
            return null;
        }
    }

    /// <summary>Resolve a hit-test result to the row it belongs to.</summary>
    private static FileRow? ItemUnder(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListViewItem { DataContext: FileRow row } && row.IsFolder) return row;
            if (source is FrameworkElement { DataContext: FileRow row2 }) return row2;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    // --- helpers ------------------------------------------------------------------------------

    /// <summary>Select the row with this node key again, so a command's effect stays visible.</summary>
    private void Reselect(string key)
    {
        var row = _rows.FirstOrDefault(r => r.Node.Key == key);
        if (row is null) return;
        FileList.SelectedItem = row;
        FileList.ScrollIntoView(row);
    }

    /// <summary>Re-apply the current filter and sort, keeping the selection where possible.</summary>
    private void SortAndKeepSelection()
    {
        var keys = SelectedNodes().Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        ApplyFilterAndSort();

        FileList.SelectedItems.Clear();
        foreach (var row in _rows.Where(r => keys.Contains(r.Node.Key))) FileList.SelectedItems.Add(row);
    }

    private void OnListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-clicking a row that is not selected selects it, which is what every file manager does
        // and what makes the context menu act on what the user pointed at.
        var row = ItemUnder(e.OriginalSource as DependencyObject);
        if (row is null) return;
        if (!FileList.SelectedItems.Contains(row))
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItem = row;
        }
    }

    private void OnListMenuOpened(object sender, RoutedEventArgs e) => UpdateCommandStates();
}

/// <summary>
/// The data a drag inside the tree carries. The key identifies the exact row (so a move of one shadowed
/// file targets that archive's entry) and the path is what a drop needs to build the new name.
/// </summary>
internal static class FileDropData
{
    /// <summary>Clipboard format name; a custom format so Explorer ignores it and only this app reads it.</summary>
    public const string Format = "PkzExplorer.FileDrop";

    /// <summary>One dragged file.</summary>
    public readonly record struct Entry(string Key, string Path);
}
