using PkzTool.Core.Archives;
using PkzTool.Core.Compression;
using PkzTool.Core.Operations;
using PkzTool.Core.Settings;
using PkzTool.Core.Vfs;
using PkzTool.Core.Workspace;

namespace PkzTool.Core.Cli;

/// <summary>
/// Runs a parsed command line headlessly. Operations drive the same
/// <see cref="PkzWorkspace"/> services the GUI uses rather than a parallel implementation, so a rule
/// such as the compression threshold can only be implemented once.
/// <para>
/// Every mutating operation performs exactly one operation and saves immediately, overwriting any
/// existing file without prompting. Exit code 0 means success; anything else means at least one
/// failure, and errors go to stderr through the caller's writer.
/// </para>
/// </summary>
public sealed class CliRunner
{
    private readonly Action<string> _out;
    private readonly Action<string> _error;
    private readonly PkzSettings _settings;

    /// <summary>Create a runner writing normal output to <paramref name="output"/> and problems to
    /// <paramref name="error"/>.</summary>
    public CliRunner(Action<string> output, Action<string> error, PkzSettings settings)
    {
        _out = output;
        _error = error;
        _settings = settings;
    }

    /// <summary>
    /// Parse and run. Returns the process exit code: 0 on success, 1 on an operation failure, 2 on a
    /// usage error.
    /// </summary>
    public int Run(IReadOnlyList<string> args)
    {
        CliRequest request;
        try
        {
            request = CliParser.Parse(args);
        }
        catch (CliUsageException ex)
        {
            if (ex.Message.Length > 0) _error($"error: {ex.Message}");
            foreach (var line in CliParser.Usage().Split(Environment.NewLine))
                _error(line);
            return 2;
        }

        // A path that does not exist is a usage problem, not an operation failure: it is caught before
        // any work starts so a typo cannot half-apply an operation. create-from-folder is the exception
        // — there the pkz path is the *destination*, which is expected not to exist yet.
        if (request.Operation != CliOperation.CreateFromFolder)
        {
            var missing = request.Archives.Where(a => !File.Exists(a)).ToList();
            if (missing.Count > 0)
            {
                foreach (var path in missing) _error($"error: no such file: {path}");
                return 2;
            }
        }

        try
        {
            return request.Operation switch
            {
                CliOperation.List => RunList(request),
                CliOperation.Extract => RunExtract(request, all: false),
                CliOperation.ExtractAll => RunExtract(request, all: true),
                CliOperation.Replace => RunReplace(request),
                CliOperation.Recompress => RunRecompress(request),
                CliOperation.Add => RunAdd(request),
                CliOperation.Delete => RunDelete(request),
                CliOperation.Rename => RunRename(request),
                CliOperation.CreateFromFolder => RunCreateFromFolder(request),
                _ => throw new CliUsageException($"unsupported operation {request.Operation}"),
            };
        }
        catch (CliUsageException ex)
        {
            _error($"error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            // Anything unexpected is an operation failure; the message is the useful part.
            _error($"error: {ex.Message}");
            return 1;
        }
    }

    // --- operations -----------------------------------------------------------------------

    private int RunList(CliRequest request)
    {
        using var workspace = OpenWorkspace(request);
        var nodes = SelectedOrAll(workspace, request);
        var rows = EntryListing.Rows(nodes);

        _out(EntryListing.ToText(rows).TrimEnd());
        if (request.Verbose)
            _out($"{rows.Count} entries from {request.Archives.Count} archive(s)");

        if (request.Csv is { } csv)
        {
            if (request.DryRun)
            {
                _out($"[dry-run] would write {csv}");
            }
            else
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(csv));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(csv, EntryListing.ToCsv(rows));
                _out($"wrote {csv}");
            }
        }

        return 0;
    }

    private int RunExtract(CliRequest request, bool all)
    {
        using var workspace = OpenWorkspace(request);
        var nodes = all ? workspace.AllFiles.ToList() : SelectedOrAll(workspace, request);
        if (nodes.Count == 0)
        {
            _error("error: nothing matched the --entry selectors");
            return 1;
        }

        // The CLI always overwrites, which the brief calls out explicitly; no prompt is possible.
        if (request.DryRun)
        {
            foreach (var node in nodes)
                _out($"[dry-run] would extract {EntrySelector.QualifiedName(node)}");
            _out($"[dry-run] {nodes.Count} entries to {request.Out}");
            return 0;
        }

        var summary = ExtractionService.Extract(workspace, nodes, request.Out!,
            prompt: null, progress: VerboseProgress(request));
        foreach (var (path, error) in summary.Errors) _error($"error: {path}: {error}");
        _out($"{summary.Describe()} to {request.Out}");
        return summary.Failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Replace entries from files in <c>--out</c>. A selected entry is matched to a file of the same
    /// relative path, so extracting a subtree, editing it and replacing it back is the intended flow.
    /// <para>
    /// Each entry is written back into <em>the archive it came from</em>, which is what makes a single
    /// invocation able to replace entries across several loaded archives. <c>--pkz</c> narrows the work
    /// to one archive rather than redirecting every entry into it.
    /// </para>
    /// </summary>
    private int RunReplace(CliRequest request)
    {
        using var workspace = OpenWorkspace(request);
        var nodes = SelectedOrAll(workspace, request);
        if (nodes.Count == 0)
        {
            _error("error: nothing matched the --entry selectors");
            return 1;
        }

        string sourceRoot = Path.GetFullPath(request.Out!);
        var restriction = ResolveTargetArchive(workspace, request, allowAny: true);
        var (compression, level) = (request.EffectiveCompression, request.EffectiveLevel);

        var missing = new List<string>();
        var planned = new List<(VfsNode Node, string File, LoadedArchive Archive)>();
        foreach (var node in nodes)
        {
            // The file is looked up under the same relative path, e.g. pl/pl0000.dat under --out.
            string candidate = Path.Combine(sourceRoot, node.Path.Replace(VirtualPath.Separator,
                Path.DirectorySeparatorChar));
            if (!File.Exists(candidate))
            {
                missing.Add(candidate);
                continue;
            }

            // The entry goes back where it came from; --pkz, when given, limits which entries qualify.
            var owner = workspace.Archives.FirstOrDefault(a => ReferenceEquals(a.Archive, node.Owner));
            if (owner is null) continue;
            if (restriction is not null && !ReferenceEquals(owner, restriction)) continue;

            planned.Add((node, candidate, owner));
        }

        // A replacement that cannot find its file is a failure the user must see; silently skipping it
        // would look like success while nothing changed.
        if (missing.Count > 0)
        {
            foreach (var path in missing) _error($"error: no such file: {path}");
            return 1;
        }

        if (planned.Count == 0)
        {
            _error("error: no selected entry belongs to the archive named by --pkz");
            return 1;
        }

        foreach (var (node, file, owner) in planned)
        {
            if (request.Verbose) _out($"replace {node.Path} <- {file} (in {owner.FileName})");
            if (request.DryRun) continue;
            byte[] data = File.ReadAllBytes(file);
            workspace.StageAddOrReplace(owner, node.Path, data, compression, level,
                allowOverwrite: true, replaceKey: node.Key);
        }

        // The count names the archives touched, so a multi-archive run is visible rather than implied.
        int archives = planned.Select(p => p.Archive).Distinct().Count();
        return FinishMutation(request, workspace,
            $"replaced {planned.Count} entr{(planned.Count == 1 ? "y" : "ies")} in {archives} archive(s)");
    }

    private int RunRecompress(CliRequest request)
    {
        using var workspace = OpenWorkspace(request);
        var nodes = SelectedOrAll(workspace, request);
        if (nodes.Count == 0)
        {
            _error("error: nothing matched the --entry selectors");
            return 1;
        }

        var compression = request.EffectiveCompression;
        int touched = 0;
        foreach (var node in nodes)
        {
            if (request.Verbose)
                _out($"recompress {node.Path} to {CompressionCodec.DisplayName(compression)}");
            if (request.DryRun) continue;
            workspace.StageRecompress(node, compression, request.EffectiveLevel);
            touched++;
        }

        return FinishMutation(request, workspace, $"recompressed {touched} entr{(touched == 1 ? "y" : "ies")}");
    }

    /// <summary>Add files or a whole folder, preserving the source shape under the virtual root.</summary>
    private int RunAdd(CliRequest request)
    {
        using var workspace = OpenWorkspace(request);
        // An add has to land in one named archive, so the first loaded one is the default target.
        var archive = ResolveTargetArchive(workspace, request)!;
        var (compression, level) = (request.EffectiveCompression, request.EffectiveLevel);
        string source = Path.GetFullPath(request.Out!);

        if (!File.Exists(source) && !Directory.Exists(source))
        {
            _error($"error: no such file or folder: {source}");
            return 1;
        }

        if (request.DryRun)
        {
            var preview = File.Exists(source)
                ? new[] { source }
                : Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray();
            foreach (var file in preview) _out($"[dry-run] would add {file}");
            _out($"[dry-run] {preview.Length} file(s) into {archive.FileName}");
            return 0;
        }

        // A single file keeps its own name at the root; a folder contributes its contents, so the
        // virtual path is the path relative to the dragged folder rather than including it.
        AddSummary summary = File.Exists(source)
            ? ExtractionService.AddFiles(workspace, archive, new[] { source },
                Path.GetDirectoryName(source)!, virtualFolder: null, compression, level,
                confirmOverwrite: null, progress: VerboseProgress(request))
            : ExtractionService.AddFolder(workspace, archive, source, virtualFolder: null, compression,
                level, confirmOverwrite: null, progress: VerboseProgress(request));

        foreach (var (path, error) in summary.Errors) _error($"error: {path}: {error}");
        int code = FinishMutation(request, workspace, summary.Describe());
        return summary.Errors.Count > 0 ? 1 : code;
    }

    private int RunDelete(CliRequest request)
    {
        using var workspace = OpenWorkspace(request);
        var nodes = SelectedOrAll(workspace, request);
        if (nodes.Count == 0)
        {
            _error("error: nothing matched the --entry selectors");
            return 1;
        }

        if (request.Verbose)
            foreach (var node in nodes)
                _out($"delete {node.Path}");

        if (request.DryRun)
            return FinishMutation(request, workspace, $"would delete {nodes.Count} entr{(nodes.Count == 1 ? "y" : "ies")}");

        int removed = workspace.StageDelete(nodes);
        return FinishMutation(request, workspace, $"deleted {removed} entr{(removed == 1 ? "y" : "ies")}");
    }

    private int RunRename(CliRequest request)
    {
        using var workspace = OpenWorkspace(request);
        var restriction = ResolveTargetArchive(workspace, request, allowAny: true);

        if (request.Entries.Count == 1 &&
            VirtualPath.Normalize(request.Entries[0]) is { } folderPath &&
            workspace.FindFolder(folderPath) is { Path.Length: > 0 } folder &&
            !workspace.AllFiles.Any(f => VirtualPath.Comparer.Equals(f.Path, folderPath)))
        {
            var folderFiles = folder.DescendantFiles()
                .Where(f => restriction is null || ReferenceEquals(f.Owner, restriction.Archive))
                .ToList();
            if (folderFiles.Count == 0)
            {
                _error(restriction is not null
                    ? "error: no selected entry belongs to the archive named by --pkz"
                    : "error: nothing matched the --entry selectors");
                return 1;
            }

            if (request.DryRun)
                return FinishMutation(request, workspace, $"would rename {folder.Path} to {request.Name}");

            int folderRenamed = workspace.StageRename(folder, request.Name!, restriction);
            return FinishMutation(request, workspace, $"renamed {folderRenamed} entr{(folderRenamed == 1 ? "y" : "ies")}");
        }

        var nodes = SelectedOrAll(workspace, request);
        if (nodes.Count == 0)
        {
            _error("error: nothing matched the --entry selectors");
            return 1;
        }

        if (restriction is not null)
        {
            nodes = nodes.Where(n => ReferenceEquals(n.Owner, restriction.Archive)).ToList();
            if (nodes.Count == 0)
            {
                _error("error: no selected entry belongs to the archive named by --pkz");
                return 1;
            }
        }

        // Renaming several entries at once onto one name cannot work, so the CLI takes exactly one.
        if (nodes.Count > 1)
        {
            _error($"error: --rename takes exactly one entry; {nodes.Count} matched. " +
                   "A folder selector renames the whole subtree.");
            return 1;
        }

        var node = nodes[0];
        if (request.DryRun)
            return FinishMutation(request, workspace, $"would rename {node.Path} to {request.Name}");

        int renamed = workspace.StageRename(node, request.Name!, restriction);
        return FinishMutation(request, workspace, $"renamed {renamed} entr{(renamed == 1 ? "y" : "ies")}");
    }

    private int RunCreateFromFolder(CliRequest request)
    {
        string source = Path.GetFullPath(request.Out!);
        string destination = Path.GetFullPath(request.Archives[0]);

        if (!Directory.Exists(source))
        {
            _error($"error: no such folder: {source}");
            return 1;
        }

        var (compression, level) = (request.EffectiveCompression, request.EffectiveLevel);

        if (request.DryRun)
        {
            int count = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count();
            _out($"[dry-run] would pack {count} file(s) from {source} into {destination} " +
                 $"as {CompressionCodec.DisplayName(compression)}");
            return 0;
        }

        // Oodle needs the dll; the CLI cannot prompt, so a missing dll fails with the path it looked for.
        string? oodle = null;
        if (compression == EntryCompression.OodleKraken)
        {
            oodle = _settings.ResolveOodleDll();
            if (oodle is null)
            {
                _error("error: Oodle compression was requested but no oodle dll was found. " +
                       "Set \"OodleDllPath\" in settings.json next to the exe.");
                return 1;
            }
        }

        var result = PkzFolderBuilder.Build(source, destination, compression, level, oodle,
            request.ForceCompression, VerboseProgress(request));
        foreach (var (path, error) in result.Errors) _error($"error: {path}: {error}");
        _out($"created {destination}: {result.FileCount} entries, {result.TotalBytes} bytes in, " +
             $"{result.ArchiveBytes} bytes out");
        return result.Errors.Count > 0 ? 1 : 0;
    }

    // --- helpers --------------------------------------------------------------------------

    /// <summary>
    /// Save after a mutation unless this is a dry run, and report the outcome. One operation per
    /// invocation, saved immediately, is what the brief specifies.
    /// </summary>
    private int FinishMutation(CliRequest request, PkzWorkspace workspace, string message)
    {
        if (request.DryRun)
        {
            _out($"[dry-run] {message}");
            return 0;
        }

        if (!workspace.IsModified)
        {
            _out(message);
            return 0;
        }

        var outcome = workspace.Save();
        foreach (var (path, error) in outcome.Failures) _error($"error: {path}: {error}");
        _out(message + (outcome.Written > 0 ? $"; saved {outcome.Written} archive(s)" : string.Empty));
        return outcome.Failures.Count > 0 ? 1 : 0;
    }

    /// <summary>Open every archive the request names as one virtual filesystem.</summary>
    private PkzWorkspace OpenWorkspace(CliRequest request)
    {
        var workspace = new PkzWorkspace
        {
            OodlePath = _settings.ResolveOodleDll(),
            // --force-compression is a save-time policy, so it rides on the workspace the operation
            // mutates and applies to exactly the entries that operation touches.
            ForceCompression = request.ForceCompression,
        };
        var result = workspace.Load(request.Archives, workspace.OodlePath);
        foreach (var (path, error) in result.Failures) _error($"error: {path}: {error}");
        if (result.Loaded == 0)
            throw new CliUsageException("none of the given pkz files could be opened");
        return workspace;
    }

    /// <summary>Resolve the selected rows, failing loudly when a selector matched nothing.</summary>
    private List<VfsNode> SelectedOrAll(PkzWorkspace workspace, CliRequest request)
    {
        var all = workspace.AllFiles.ToList();
        if (request.Entries.Count == 0) return all;

        var unmatched = EntrySelector.Unmatched(all, request.Entries);
        // A selector that matches nothing is almost always a typo; reporting it beats silently doing
        // part of what was asked.
        if (unmatched.Count > 0)
            throw new CliUsageException("no entry matched: " + string.Join(", ", unmatched));

        return EntrySelector.MatchAll(all, request.Entries);
    }

    /// <summary>
    /// The archive an add or rename targets: <c>--pkz</c> by file name or path, otherwise the first
    /// loaded archive.
    /// <para>
    /// With <paramref name="allowAny"/> the absence of <c>--pkz</c> yields null instead of the first
    /// archive. Replace uses that to put each entry back into the archive it came from; every other
    /// operation adds into one archive, so it needs a definite target.
    /// </para>
    /// </summary>
    private LoadedArchive? ResolveTargetArchive(PkzWorkspace workspace, CliRequest request,
        bool allowAny = false)
    {
        if (string.IsNullOrWhiteSpace(request.Pkz))
            return allowAny ? null : workspace.Archives[0];

        var match = workspace.Archives.FirstOrDefault(a =>
            string.Equals(a.FileName, request.Pkz, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Path, request.Pkz, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new CliUsageException(
                $"no loaded pkz matches --pkz '{request.Pkz}'; loaded: " +
                string.Join(", ", workspace.Archives.Select(a => a.FileName)));
        return match;
    }

    private Action<OperationProgress>? VerboseProgress(CliRequest request) =>
        request.Verbose ? p => _out($"  {p.Completed}/{p.Total} {p.Message}") : null;
}
