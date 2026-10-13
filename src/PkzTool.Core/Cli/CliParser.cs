using PkzTool.Core.Compression;

namespace PkzTool.Core.Cli;

/// <summary>Raised for a malformed command line; the CLI reports it to stderr and exits nonzero.</summary>
public sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message)
    {
    }
}

/// <summary>
/// Parses the command line. The shape is fixed: one operation flag, then options, then PKZ paths. Once
/// a bare argument appears the parser stops looking for flags, so a file name that begins with a dash
/// or collides with an option name is still usable as the last argument.
/// </summary>
public static class CliParser
{
    private static readonly Dictionary<string, CliOperation> Operations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--list"] = CliOperation.List,
        ["--extract"] = CliOperation.Extract,
        ["--extract-all"] = CliOperation.ExtractAll,
        ["--replace"] = CliOperation.Replace,
        ["--recompress"] = CliOperation.Recompress,
        ["--add"] = CliOperation.Add,
        ["--delete"] = CliOperation.Delete,
        ["--rename"] = CliOperation.Rename,
        ["--create-from-folder"] = CliOperation.CreateFromFolder,
    };

    /// <summary>
    /// The single-letter aliases, matched case-sensitively. This matters because the brief gives
    /// <c>-x</c> and <c>-X</c> to two different operations (extract and extract-all), so folding case
    /// would make one of them unreachable.
    /// </summary>
    private static readonly Dictionary<string, CliOperation> ShortFlags = new(StringComparer.Ordinal)
    {
        ["-l"] = CliOperation.List,
        ["-x"] = CliOperation.Extract,
        ["-X"] = CliOperation.ExtractAll,
        ["-r"] = CliOperation.Replace,
        ["-c"] = CliOperation.Recompress,
        ["-a"] = CliOperation.Add,
        ["-d"] = CliOperation.Delete,
        ["-n"] = CliOperation.Rename,
        ["-f"] = CliOperation.CreateFromFolder,
    };

    /// <summary>Resolve an operation flag, honouring the case of a single-letter alias.</summary>
    private static bool TryGetOperation(string arg, out CliOperation operation)
    {
        if (arg.Length == 2 && arg[0] == '-')
            return ShortFlags.TryGetValue(arg, out operation);
        return Operations.TryGetValue(arg, out operation);
    }

    /// <summary>
    /// The option flags, in the order the help text lists them. Exposed so <c>--help</c> and the parser
    /// cannot drift apart.
    /// </summary>
    public static readonly string[] OptionFlags =
    {
        "--entry <path>", "--out <dir>", "--pkz <name>", "--name <new>", "--csv <file>",
        "--compression <type>", "--level <n>", "--force-compression",
        "--dry-run", "--verbose",
    };

    /// <summary>
    /// Parse <paramref name="args"/>. <paramref name="settings"/> supplies the Oodle path and the
    /// default compression for <c>-f</c>, which the CLI honours exactly as the GUI does.
    /// </summary>
    public static CliRequest Parse(IReadOnlyList<string> args)
    {
        var request = new CliRequest { Operation = CliOperation.List, OperationFlag = string.Empty };
        string? operationFlag = null;
        bool pathsStarted = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];

            // "the operation flag and options first, PKZ paths last": the first bare argument ends the
            // option region, so everything after it is a path even if it looks like a flag.
            if (pathsStarted || !arg.StartsWith('-') || arg == "-")
            {
                pathsStarted = true;
                request.Archives.Add(arg);
                continue;
            }

            if (TryGetOperation(arg, out var operation))
            {
                if (operationFlag is not null)
                    throw new CliUsageException(
                        $"only one operation is allowed per invocation ('{operationFlag}' and '{arg}' were both given)");
                operationFlag = arg;
                request.Operation = operation;
                request.OperationFlag = arg;
                continue;
            }

            switch (arg.ToLowerInvariant())
            {
                case "--entry":
                    request.Entries.Add(Require(args, ref i, arg));
                    break;

                case "--out":
                    request.Out = Require(args, ref i, arg);
                    break;

                case "--pkz":
                    request.Pkz = Require(args, ref i, arg);
                    break;

                case "--name":
                    request.Name = Require(args, ref i, arg);
                    break;

                case "--csv":
                    request.Csv = Require(args, ref i, arg);
                    break;

                case "--level":
                {
                    string text = Require(args, ref i, arg);
                    if (!int.TryParse(text, out int level))
                        throw new CliUsageException($"--level expects a whole number, got '{text}'");
                    request.Level = level;
                    break;
                }

                case "--compression":
                {
                    string text = Require(args, ref i, arg);
                    var kind = CompressionCodec.TryParseKind(text)
                        ?? throw new CliUsageException(
                            $"unknown compression '{text}' (use none, zstandard or oodle)");
                    request.Compression = kind;
                    break;
                }

                case "--force-compression":
                    request.ForceCompression = true;
                    break;

                case "--dry-run":
                    request.DryRun = true;
                    break;

                case "--verbose":
                    request.Verbose = true;
                    break;

                case "--help":
                case "-h":
                    throw new CliUsageException(string.Empty); // the caller prints usage and exits 0

                default:
                    throw new CliUsageException($"unknown option '{arg}'");
            }
        }

        if (operationFlag is null)
            throw new CliUsageException("no operation flag was given");

        Validate(request);
        return request;
    }

    /// <summary>True when the arguments ask for help rather than an operation.</summary>
    public static bool IsHelp(IReadOnlyList<string> args) =>
        args.Any(a => a is "--help" or "-h" or "--?" or "/?");

    /// <summary>
    /// Check the option set against the operation. Catching a missing or meaningless option here means
    /// the runner can assume everything it reads is present.
    /// </summary>
    private static void Validate(CliRequest request)
    {
        void RequireArchives()
        {
            if (request.Archives.Count == 0)
                throw new CliUsageException($"{request.OperationFlag} needs at least one pkz path");
        }

        switch (request.Operation)
        {
            case CliOperation.List:
                RequireArchives();
                break;

            case CliOperation.Extract:
                RequireArchives();
                RequireEntries(request);
                RequireOut(request);
                break;

            case CliOperation.ExtractAll:
                RequireArchives();
                RequireOut(request);
                break;

            case CliOperation.Replace:
                RequireArchives();
                RequireEntries(request);
                RequireOut(request, "the folder holding the replacement files");
                break;

            case CliOperation.Recompress:
                RequireArchives();
                RequireEntries(request);
                if (request.Compression is null)
                    throw new CliUsageException("--recompress needs --compression (none, zstandard or oodle)");
                break;

            case CliOperation.Add:
                RequireArchives();
                RequireOut(request, "the file or folder to add");
                break;

            case CliOperation.Delete:
                RequireArchives();
                RequireEntries(request);
                break;

            case CliOperation.Rename:
                RequireArchives();
                RequireEntries(request);
                if (string.IsNullOrWhiteSpace(request.Name))
                    throw new CliUsageException("--rename needs --name <new name>");
                break;

            case CliOperation.CreateFromFolder:
                // The folder is the source and the single pkz path is the destination.
                if (request.Out is null && request.Archives.Count == 1)
                    throw new CliUsageException("--create-from-folder needs --out <folder> and one destination pkz path");
                if (request.Out is null)
                    throw new CliUsageException("--create-from-folder needs --out <folder>");
                if (request.Archives.Count != 1)
                    throw new CliUsageException("--create-from-folder takes exactly one destination pkz path");
                break;
        }
    }

    private static void RequireEntries(CliRequest request)
    {
        if (request.Entries.Count == 0)
            throw new CliUsageException($"{request.OperationFlag} needs at least one --entry <path>");
    }

    private static void RequireOut(CliRequest request, string what = "the destination folder")
    {
        if (string.IsNullOrWhiteSpace(request.Out))
            throw new CliUsageException($"{request.OperationFlag} needs --out ({what})");
    }

    private static string Require(IReadOnlyList<string> args, ref int index, string flag)
    {
        if (index + 1 >= args.Count)
            throw new CliUsageException($"{flag} needs a value");
        index++;
        return args[index];
    }

    /// <summary>The help text, printed for <c>--help</c> and on a usage error.</summary>
    public static string Usage() => string.Join(Environment.NewLine, new[]
    {
        "PKZ Explorer - Bayonetta 3 pkz archive explorer and editor",
        "",
        "usage: \"PKZ Explorer.exe\"                       open the GUI",
        "       \"PKZ Explorer.exe\" <operation> [options] <pkz...>",
        "",
        "operations (exactly one):",
        "  --list, -l                 list entries as columns",
        "  --extract, -x              extract the selected entries",
        "  --extract-all, -X          extract every entry",
        "  --replace, -r              replace entries from files in --out",
        "  --recompress, -c           change the compression of entries",
        "  --add, -a                  add files or a folder from --out",
        "  --delete, -d               delete entries",
        "  --rename, -n               rename one entry",
        "  --create-from-folder, -f   build a new pkz from --out",
        "",
        "options:",
        "  --entry <path>             repeatable: an exact path, a folder, or a glob (pl/*.dat).",
        "                             A duplicated path is named \"pl/pl0000.dat - data.pkz\".",
        "  --out <dir>                destination folder (extract) or source (replace, add, -f)",
        "  --pkz <name>               which loaded pkz an add or rename targets, by file name",
        "  --name <new>               the new name for --rename (a file or folder name)",
        "  --csv <file>               export the --list output to a CSV file",
        "  --compression <type>       none (default), zstandard or oodle",
        "  --level <n>                codec level (zstd 1-22, oodle 1-9)",
        "  --force-compression        keep the requested type even when it does not shrink the file",
        "  --dry-run                  report what would happen without writing",
        "  --verbose                  list each file as it is handled",
        "",
        "example:",
        "  \"PKZ Explorer.exe\" -x --entry \"pl/*.dat\" --out C:\\out data000.pkz data001.pkz",
        "",
        "with no arguments at all the GUI opens. Any argument runs headless.",
        "the Oodle dll comes from settings.json next to the exe.",
    });
}
