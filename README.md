# PKZ Explorer

A PKZ archive explorer and editor for Bayonetta 3 (Switch).  
Multiple archives open as one continuous virtual filesystem. All edits are staged in memory and only written when you save.

Requires the .NET 8 Desktop Runtime (x64) and a 64-bit `oo2core_*.dll` (Oodle). The dll is proprietary and is not bundled — point Settings at your own copy.

## Quick start

1. Download the latest `PkzExplorer-win-x64.zip` and extract it.
2. Place a 64-bit `oo2core_*.dll` somewhere the tool can find it (or set the path in Settings).
3. Run `PKZ Explorer.exe`.
4. Open one or more `.pkz` files (File → Open, drag-and-drop, or double-click a pkz).
5. Browse, extract, replace, add, rename, or delete entries.
6. Save when done (Ctrl+S).

Settings are stored next to the executable so the tool stays portable.

## Features

### Viewing

- Multiple pkz files show up as one merged tree.
- If the same path exists in more than one archive, later ones get a suffix (` - archive.pkz`).
- Searching files
- Sorting view (does not affect how files are stored)

### Editing

- Add, Replace, Recompress, Rename, Delete, and Revert.
- Drag files in from Explorer to add them; drag out to extract.
- Drag inside the tree to move things between folders (same archive only).
- Changed rows are marked with a `*` and a light tint. Nothing touches disk until you save.

### Compression

- None, ZStandard, or Oodle Kraken.
- If compression doesn’t shrink a file by at least 10%, it gets stored uncompressed instead (unless you force it). This is better for faster loading ingame.

### Extraction

- Extract selected items or entire archive.
- Folder structure is preserved.

## CLI

Any arguments make it run headless. No arguments opens the GUI.

```
"PKZ Explorer.exe" <operation> [options] <pkz...>
```

### Operations (pick one)

| Flag | Alias | What it does |
|------|-------|--------------|
| `--list` | `-l` | List entries |
| `--extract` | `-x` | Extract selected entries |
| `--extract-all` | `-X` | Extract everything |
| `--replace` | `-r` | Replace entries from files |
| `--recompress` | `-c` | Change compression |
| `--add` | `-a` | Add files or a folder |
| `--delete` | `-d` | Delete entries |
| `--rename` | `-n` | Rename one entry |
| `--create-from-folder` | `-f` | Build a new pkz from a folder |

### Useful options

| Flag | Meaning |
|------|---------|
| `--entry <path>` | Exact path, folder, or glob (`pl/*.dat`). Can be repeated. |
| `--out <dir>` | Where to extract to, or where to take files from. |
| `--pkz <name>` | Which archive to add into. |
| `--name <new>` | New name when renaming. |
| `--compression <type>` | `none` (default), `zstandard`, or `oodle`. |
| `--level <n>` | Codec level. |
| `--force-compression` | Keep the requested type even if it doesn’t shrink the file. |
| `--dry-run` | Show what would happen without writing. |
| `--verbose` | Print each file as it’s handled. |
| `--csv <file>` | Write the list as CSV. |

### Examples

```
"PKZ Explorer.exe" -l data000.pkz
"PKZ Explorer.exe" -x --entry "pl/*.dat" --out C:\out data000.pkz
"PKZ Explorer.exe" -a --out C:\newfiles --pkz data000.pkz data000.pkz
```

## Building / contributing

The tool shares types with XT1 Studio (`Xt1.Core`). Place the two repositories side by side:

```
exefs\
  Xt1Tool\
  PkzTool\
```

Or point the build at the Xt1.Core project:

```
dotnet build PkzTool.sln -p:Xt1CoreProject=C:\path\to\Xt1Tool\src\Xt1.Core\Xt1.Core.csproj
```

Build and test:

```
dotnet build PkzTool.sln
dotnet test  PkzTool.sln
```

Publish a release:

```
publish-release.bat
```

Produces a single framework-dependent `PKZ Explorer.exe` plus source and binary zips. The Oodle dll is never bundled.
