using PkzTool.Core.Workspace;

namespace PkzTool.Core.Tests;

/// <summary>
/// Opens workspaces for a test and disposes them when it ends, so a test never has to remember the
/// teardown. Failures during load are surfaced as an assertion rather than silently producing an empty
/// view, which would otherwise turn a broken fixture into a confusing "no rows" failure.
/// </summary>
public sealed class PkzWorkspaceHarness : IDisposable
{
    private readonly List<PkzWorkspace> _opened = new();

    /// <summary>Open a workspace over the given archives, asserting they all loaded.</summary>
    public PkzWorkspace Open(params string[] paths)
    {
        var workspace = new PkzWorkspace();
        var result = workspace.Load(paths);
        _opened.Add(workspace);

        if (result.Failures.Count > 0)
        {
            var detail = string.Join("; ", result.Failures.Select(f => $"{f.Path}: {f.Error}"));
            throw new InvalidOperationException($"the fixture failed to load: {detail}");
        }

        return workspace;
    }

    /// <summary>Open a workspace without asserting, for a test that expects a failure.</summary>
    public (PkzWorkspace Workspace, PkzWorkspace.LoadResult Result) OpenExpectingFailure(params string[] paths)
    {
        var workspace = new PkzWorkspace();
        var result = workspace.Load(paths);
        _opened.Add(workspace);
        return (workspace, result);
    }

    public void Dispose()
    {
        foreach (var workspace in _opened) workspace.Dispose();
        _opened.Clear();
    }
}
