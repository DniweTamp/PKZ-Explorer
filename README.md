# PKZ Explorer

A pkz archive explorer and editor for **Bayonetta 3** (Switch), as a C#/WPF app plus a headless CLI
in the same executable.

Several loaded archives are presented as **one continuous virtual filesystem**. Every edit is staged
in memory and nothing touches disk until you save.

The visual design language — palette, control templates, theming, dialog conventions — is taken from
the neighbouring `Xt1Tool` project (`src/Xt1.App.Wpf`), and `Xt1.Core` is referenced for the theme
enum and the shared byte formatter, so the two tools cannot drift apart.

---

## Layout

| Project | Purpose |
|---|---|
| `src/PkzTool.Core` | The pkz format, compression, the virtual filesystem, the staged-edit model, file operations, and the CLI parser/runner. No WPF. |
| `src/PkzTool.App.Wpf` | The WPF shell: list view, dialogs, drag and drop, and the headless entry point. |
| `tests/PkzTool.Core.Tests` | 141 tests over synthetic fixtures, including the compression rule, the merged-filesystem naming rules, and edit composition. |

Build and test:

```
dotnet build PkzTool.sln
dotnet test  PkzTool.sln
```

---

## Repository layout

`PkzTool` shares types with **XT1 Studio** (`Xt1.Core`), so that repository has to be checked out
too. Development assumes the two sit side by side:

```
exefs\
  Xt1Tool\        <- the XT1 Studio checkout, providing Xt1.Core
  PkzTool\        <- this repository
```

Anywhere else, point the build at the checkout:

```
dotnet build PkzTool.sln -p:Xt1CoreProject=C:\path\to\Xt1Tool\src\Xt1.Core\Xt1.Core.csproj
```

The same value can be set once in the `XT1_CORE_PROJECT` environment variable. If `Xt1.Core` cannot
be found the build stops with one sentence saying so, rather than a dozen "namespace Xt1 could not be
found" errors in unrelated files.

Line endings are pinned by `.gitattributes`: LF in the repository, CRLF for `.bat`, and binary for
the icon and archives, so a checkout does not rewrite every file on the way in.

---

## Publishing a release

```
publish-release.bat                 publish, then write src.zip and PkzExplorer-win-x64.zip
publish-release.bat /noarchives     publish only, skip both archives
publish-release.bat /nopause        do not wait for a keypress at the end
```

The result is a **single `PKZ Explorer.exe`** (framework-dependent, win-x64, ~3.6 MB) in
`src\PkzTool.App.Wpf\bin\Release\net8.0-windows\publish\win-x64\`, and two archives beside the
script:

| Artifact | Contents |
|---|---|
| `src.zip` | The tracked files at HEAD (`git archive`) |
| `PkzExplorer-win-x64.zip` | The published build folder |

The target machine needs the **.NET 8 Desktop Runtime (x64)**; nothing else is bundled.

The script rebuilds the output folder every time, keeps only the expected executable, and then runs
two checks against the published build before reporting success:

* `--uitest`, which builds the shell and every dialog and exercises the interactive behaviour — this
  is what catches a resource a single-file publish dropped — and confirms every dialog shows its own
  button row;
* `--help`, which confirms the command line still works from the published exe.

It then deletes the `settings.json` those checks wrote, because the tool keeps its state beside the
executable and that file is the publisher's own settings, not something to ship.

`src.zip` archives **HEAD**, so uncommitted work is not in it. That is deliberate — the archive
should describe a build that exists in the history — and the script says so when the tree is dirty
rather than shipping a zip that does not match the binary.

**The Oodle dll is not bundled.** Oodle is proprietary, so the user points Settings at their own
`oo2core_*.dll`. Without one, Oodle entries cannot be read or written; `None` and `ZStandard`
archives work normally.

---

## The format

A pkz is a flat container with no compression at the file-system level; compression is per entry.

```
offset  size  field
0x00    4     magic "pkzl"
0x04    4     version (0x00020000)
0x08    8     file size
0x10    4     entry count
0x14    4     entry size (0x20)
0x18    4     string area size
0x1C    4     string area base (0x1000 in files written by PKZe; readers ignore it)
0x20    ...   entry table: entryCount * entrySize bytes
              entry: u32 nameOffset | u32 compOffset | u64 decompressedSize
                     | u64 dataOffset | u64 compressedSize
        ...   string area: NUL-terminated strings, 8-byte aligned
        ...   data, each entry 64-byte aligned
```

Entry name and compression offsets are relative to the start of the string area; data offsets are
absolute. `comp` is `"None"`, `"ZStandard"` or `"OodleKraken"`. Names are stored in the archive, so
entries are addressed by their path (`pl/pl0000.dat`) with no hash lookup.

Two consequences the code depends on:

* **Entry order is meaningful.** The writer preserves the order it is given rather than sorting by
  name. The list view's default sort is "PKZ order", so a save must not reshuffle an archive.
* **An entry the user did not touch is copied through byte for byte**, with its stored compression
  label. A save is not a repack: re-encoding untouched entries would be slower and could change bytes
  nobody asked to change.

---

## The compression rule

After compressing a file, if the compressed size is **90% or more of the original**, the file is
stored uncompressed instead. This overrides the type picked in the dialog, **for that file only**, so
a requested compression can never make an archive bigger than storing the file as-is.

It applies to Add, Replace, Recompress, PKZ from folder, and the matching CLI operations. In the CLI,
`--force-compression` skips it.

This is implemented once, in `CompressionCodec.Pack`, which returns both the bytes and the label they
must be written with — so the label can never disagree with the payload.

---

## GUI

### File menu

* **Open** — multi-select. Replaces everything currently loaded; there is no way to add to the loaded
  view. A file that fails to load is skipped, reported, and the rest still load.
* **Open Recent** — each entry is a single pkz file, pruned of files that no longer exist.
* **Save** — writes every modified pkz.
* **Save As** — a save dialog for each loaded pkz.
* **PKZ from folder** — pick a folder, pick one compression type for the whole run, then a save
  dialog; afterwards the new pkz is loaded, replacing the view.
* **Close all** / **Exit** — prompt Save / Discard / Cancel when edits are staged.

### The list

* Sortable columns: **filename, size (uncompressed), file format, compression**. Format comes from the
  file extension only.
* The default order is folders first, then files **in PKZ order**. Sorting is view-only and never
  changes the order stored in the archive.
* Rows form a real folder tree built from the actual paths, forward slashes everywhere
  (`pl/pl0000.dat`, `effect/texture/001.wta`). Double-click expands or collapses a folder and does
  nothing on a file.
* Multiple pkz form one continuous filesystem with **no per-pkz separation**. A path that exists in
  several archives is one row per archive, with `" - pkzname.pkz"` appended to every row after the
  first.
* A search box filters the list. A folder is kept whenever anything under it matches, so a match is
  never shown without the path leading to it.
* Folder rows are selectable, and every action applies to everything under them.
* Staged rows are marked with `*` and tinted; the window title carries a `*` too.
* Column widths start fixed, are resizable, and are remembered in the settings file.

### Toolbar

**Open**, **Save**, a path filter, and the theme toggle. Nothing else: every other action is on the
Edit menu and the list's context menu, where the entries can be named precisely ("Extract selected"
and "Extract all" rather than a bare "Extract") and can be disabled when they do not apply.

### Context menu, editing, drag and drop

| Action | Rules |
|---|---|
| **Extract selected** | Ctrl-click, Shift+Up/Down, Ctrl+A. Recreates the full folder structure on disk. If files exist, one prompt covers the run: overwrite / skip / cancel. |
| **Extract all** | Same layout and overwrite rules. |
| **Replace** | Single selection, disabled on folders. A dialog picks the compression each time. |
| **Recompress** | Multi-select. One dialog picks the type for everything selected. |
| **Add** | Goes into the selected folder (the root when nothing is selected). One dialog asks which loaded pkz, a second picks the compression. |
| **Delete** | Always asks for confirmation, and says what a folder will take with it. |
| **Rename** | Single selection. On a file it changes the file name only; on a folder it renames that path segment for every file under it. A spelling-only rename (`Data.dat` → `data.dat`) is allowed. |
| **Revert** | Undo every staged change to the selection, leaving other rows' changes alone. The per-row counterpart to **Close all**. |
| **Name collisions** | Add, Rename and a drag-move onto an existing name in that folder of that pkz ask overwrite or cancel. Add asks **once** for the whole run, like Extract. |
| **Shadowed rows** | Delete, Replace and Rename act only on the row shown, identified by its suffix. |
| **Drag in from Explorer** | Same rules as Add, including the pkz and compression dialogs. |
| **Drag out** | Extract, only when the drop lands on Explorer. |
| **Drag within the tree** | Moves files between folders, within the same pkz only. Dropping onto another pkz's folder does nothing. |

### Shortcuts

`Ctrl+O` open · `Ctrl+S` save · `Ctrl+Shift+S` save as · `Del` delete · `F2` rename · `Ctrl+F` focus
search · `Ctrl+A` select all

### Long operations

Loading, saving, extracting, recompressing and packing show a progress bar with a **Cancel** button.
The dispatcher is pumped from the progress callback, so the window keeps repainting and Cancel stays
clickable.

### Settings

Stored in `settings.json` **next to the executable**, so the tool is portable. If that location is not
writable, a roaming copy under `%APPDATA%\PkzTool` is used instead. The file holds the Oodle dll path,
the recent-files list, the compression defaults, the column widths and the theme.

---

## Oodle

Oodle Kraken is the codec the game's own archives use. The dll is proprietary and is **never
bundled**; you supply a 64-bit `oo2core_*.dll`.

Resolution order: the configured path, then `oo2core*.dll` beside the executable, then every
directory on `PATH`. If nothing usable is found when a compression needs one, the app prompts for it
and validates the choice by actually loading it — a decompress-only dll is reported as unusable for
compression rather than failing at the end of a long pack.

Reading `None` and `ZStandard` entries never needs the dll. Neither does renaming, deleting, moving,
extracting or replacing — a rename copies the stored bytes and changes only the name.

---

## CLI

**Running the executable with any argument at all runs headless. No arguments opens the GUI.**

The operation flag and options come first, the pkz paths last. Once the first bare argument appears,
everything after it is treated as a path — so a file name that looks like a flag is still usable.

```
"PKZ Explorer.exe" -x --entry "pl/*.dat" --out C:\out data000.pkz data001.pkz
```

### Operations (exactly one)

| Flag | Alias | What it does |
|---|---|---|
| `--list` | `-l` | Print a column view |
| `--extract` | `-x` | Extract the selected entries |
| `--extract-all` | `-X` | Extract every entry |
| `--replace` | `-r` | Replace entries from files in `--out` |
| `--recompress` | `-c` | Change the compression of entries |
| `--add` | `-a` | Add files or a folder from `--out` |
| `--delete` | `-d` | Delete entries |
| `--rename` | `-n` | Rename one entry |
| `--create-from-folder` | `-f` | Build a new pkz from `--out` |

`-x` and `-X` are two different operations, so single-letter aliases are matched case-sensitively.

### Options

| Flag | Meaning |
|---|---|
| `--entry <path>` | Repeatable. An exact path, a folder, or a glob (`pl/*.dat`). Forward slashes only. |
| `--out <dir>` | Destination folder (extract) or source (replace, add, `-f`). |
| `--pkz <name>` | Which loaded pkz an add targets, by file name. |
| `--name <new>` | The new name for `--rename`. |
| `--csv <file>` | Export the `--list` output to CSV. |
| `--compression <type>` | `none` (the default), `zstandard` or `oodle`. |
| `--level <n>` | Codec level (zstd 1–22, oodle 1–9). |
| `--force-compression` | Keep the requested type even when it does not shrink the file. |
| `--dry-run` | Report what would happen without writing. |
| `--verbose` | List each file as it is handled. |

### Selecting entries

An entry is an exact virtual path, a folder (which takes its whole subtree), or a glob. In a glob `*`
and `?` stay within one path segment and `**` crosses separators, so `pl/*.dat` matches
`pl/pl0000.dat` but not `pl/sub/pl0000.dat`.

A path that exists in several pkz is named by its full path with the suffix, exactly as the GUI shows
it and exactly as `--list` prints it:

```
--entry "pl/pl0000.dat - data.pkz"
```

### Behaviour

* `--list` prints aligned columns; `--csv` adds a header row and quotes only where it must. There is
  no JSON output.
* Each mutating operation performs one operation per invocation and **saves immediately**, always
  overwriting existing files without prompting.
* An `--entry` that matches nothing fails the run rather than silently doing part of the work.
* Exit **0** on success, **nonzero** on any failure. Errors go to stderr.

### Diagnostic

`--uitest [file]` builds the shell and every dialog headlessly and reports what it checked. This is
how the XAML is verified without a human clicking through the app — it catches a broken resource key
or a constructor that throws.

`--measure-dialogs` lays every dialog out off-screen and reports whether it fits the working area,
is resizable, and shows its own button row. This is the check for the class of bug where a dialog's
content grows past its fixed height and the bottom row — the buttons — becomes unreachable.

`--check-main-layout` lays the main window out at three sizes and reports whether the list fills the
panel, whether the empty message is centred in it, and whether the columns fit without a horizontal
scrollbar. `--main-layout [pkz]` prints the same geometry as raw numbers, for when a figure looks
wrong.

`--check-dialog-buttons` checks that every dialog fits the screen, can reach its button row, and still
has a working default and cancel button — the last of these because wrapping a dialog's content in a
scroll viewer could quietly break Enter and Escape.

`--check-empty-state` confirms the panel shown when the list is empty says something true: the three
cases (nothing loaded, a search that matched nothing, and a search typed with nothing loaded) each
get their own message, and only the first claims no archive is open.

`--check-search` confirms a file inside a collapsed folder is found, and that clearing the search
restores the collapsed view.

`--check-messages` confirms every prompt is themed, sized to its content, shows its detail card only
when there is a detail, and returns immediately when suppressed instead of blocking a headless run.

`--check-badload` confirms that loading something which is not a pkz is reported rather than
leaving an empty window with nothing to explain it — the failure mode a double-click on a
corrupt file used to produce.

`--check-drop` confirms every region of the window accepts a file drop — the list, the empty area,
the toolbar, the status bar — and that dropping a pkz with none loaded opens it.

`--check-stretch` counts how many times the trailing column is re-sized over a fixed number of idle
layout passes. That stretch is driven by `LayoutUpdated`, which fires on every pass, so a change that
never converged would spin the UI thread; the check proves it settles.

`--capture-main [pkz] <out.png> [--size=WxH]` renders the main window, and `--capture-dialog <name> <out.png>` one
dialog. Names for the latter: `about`, `compression`, `overwrite`, `textprompt`, `oodle`, `pkzpick`,
`settings`. These exist because a layout can pass every assertion and still look wrong: the About
dialog's button check passed while a screenshot showed the button clipped. Captured PNGs are
git-ignored, since no image in this project is a source file.

### Main window layout

The list and the empty-state message share one `Grid` cell. They must not be siblings in a
`DockPanel`: a `DockPanel` stretches only its last child, so with the message listed second the list
was sized to its own content — it covered barely half the window — and the message centred within
that half rather than the window.

The four columns are fixed widths, and the trailing one is filled to whatever the others leave, so
the columns span the list instead of stopping part way across. That column can also be dragged: a
width the user drags is kept and honoured, and only ever reduced when it would not otherwise fit, so
dragging it narrower works and dragging it wider cannot raise a horizontal scrollbar.

The toolbar's search box is stretched to fill the space between the buttons and the theme toggle.
It must not be centred instead: a `TextBox` with no width and no content asks for about 90px, so
centring it left a sliver in the middle of an 878px column with ~394px of dead space beside it.
`--check-main-layout` asserts the box fills its column, at three window sizes.

### Search

The filter matches across the whole archive, not just what the tree is showing. While a search is
active the tree walk ignores expansion state, so a file inside a collapsed folder is still found —
that is the point of a search. Clearing it puts the collapsed view back as it was; searching never
leaves the tree expanded.

When a search matches nothing the panel says so, with a magnifier rather than the folder glyph, the
term that was searched for, and a button to clear it. Two situations leave the list blank and they
need opposite advice, so "No pkz loaded" is only used when no archive really is open.### Prompts

Every prompt goes through `Prompt.Show`, which draws the themed `MessageDialog` rather than the system
`MessageBox`. The system dialog ignores the palette and looks nothing like the rest of the tool; this
one uses the same cards, glyphs and pill buttons as every other dialog, tints its heading by severity,
and shows a scrolling monospace block for the detail — a failed load can list many files.

A prompt also never blocks a headless run. `Prompt.Suppress` is set for any headless invocation, so
instead of waiting for a click that will never come, a prompt records what it was asked and returns the
safe answer: Cancel for a cancel-able set, No for yes/no, OK otherwise. That is what makes the error
paths testable, and `--check-messages` asserts it — a suppressed prompt returns in about 2 ms.

The exception is `--show` and a double-clicked `.pkz`, which are interactive launches and get real
prompts. A message dialog is also the one dialog that shrinks to its content, so a one-line question
does not sit in the empty space a ten-line error needed.

### Dialog sizing

Dialogs are not fixed-size boxes. Every one is resizable, and `ThemedDialog` grows the window to the
height its content actually needs on first show, capped at 92% of the working area of the monitor the
dialog is on — the owner's monitor, not the primary one — so it can never run off the bottom of the
screen; anything beyond that scrolls. A dialog that manages its own layout (the About box, whose body
scrolls between a fixed header and a fixed button row) opts out with `SizesItself`.

This exists because three dialogs shipped taller than their window — the Settings dialog by 180px —
and the clipped row was always the buttons.

### Open a pkz by double-clicking it

Double-clicking a `.pkz` opens the GUI with it loaded, and so does dropping one anywhere on the
window. Both pass a plain file path, which is not a command line.

The two are told apart by how the process was started, not by its arguments — those are identical
either way. A shell launch has `explorer.exe` as its parent and opens the GUI; anything started from
a prompt or a script does not, and gets the usual headless behaviour. That matters because a bare
path is never a valid command line: every operation needs a flag, so without the distinction a shell
script that forgot its flag would wait forever on a window instead of failing with exit 2. A console
check cannot be used for this, since a GUI-subsystem executable has no console of its own even when
launched from a prompt.

To make Windows offer the app for `.pkz` files, associate the extension with the executable — for the
current user only, which needs no administrator:

```
assoc .pkz=PKZExplorer.pkz
ftype PKZExplorer.pkz="C:\path\to\PKZ Explorer.exe" "%1"
```

Without `ftype`, Windows falls back to an entry under
`HKCU\Software\Classes\Applications\PKZ Explorer.exe\shell\open\command`, which is what an "Open with"
choice writes. Either way the executable receives the path as its first argument, which is the form
described above. A **published copy must be redeployed** for a fix here to reach a double-click: the
association names a specific file, not the build output.

The window accepts a drop anywhere, not only over the list. With no archive open the list is hidden,
so a drop on the empty area is the normal way to open the first one — and a dropped `.pkz` *opens*,
while other dropped files are added to the archive already loaded.

### One documented exception to "any argument runs headless"

`--show <pkz...>` opens the GUI with those archives already loaded, and so does a bare `.pkz` path.
Those are the only arguments that do not run headless, and neither is needed for normal use.

---

## How staged edits compose

An edit is recorded against the path an entry had **when the archive was loaded**, and describes the
entry's *resulting* state rather than one operation. That is what makes sequences mean what they appear
to mean:

| Sequence | Result |
|---|---|
| Replace, then Recompress | The new bytes, stored with the new compression |
| Rename, then Replace | The new name *and* the new bytes |
| Delete, then re-Add | The new bytes, in the entry's original position |
| Rename a folder, then a file inside it (either order) | Both renames applied |
| Rename twice | The file ends at the last name |

The alternative — keying each edit by the path it happens to have at the time — silently loses the first
edit of every pair, because the second one takes its place in the lookup while the row still shows both.
Composition also means there is only **one** source of truth for staged state: the tree's rows are a
projection of it, so the view and the save cannot disagree.

A staged change that would alter nothing (a recompress to the storage an entry already has) is pruned
rather than recorded, so the archive is not rewritten and its timestamp survives.

---

## Verification

* **141 unit tests**, covering the header layout and round-trip, the 90% rule (both sides of the
  boundary, and `--force-compression`), the merged filesystem including the shadow-suffix rules, edit
  composition (every sequence in the table above), save/reload, and every CLI operation.
* **All 72 pkz files in a real Bayonetta 3 romfs parse** — 18,732 entries, 11,760 of them
  `OodleKraken` and 6,972 `None` — and a real Oodle entry inflates correctly (1,774,325 bytes →
  12,225,696).
* Save round-trips on a real archive, with untouched entries byte-identical, and the streaming writer
  produces **byte-identical output** to the in-memory layout.
* Every CLI operation was exercised against real archives and its exit code checked, including that
  `*` does not cross a path separator while `**` does, that extraction stays inside the destination
  folder, and that a selector matching nothing fails the run instead of doing part of the work.
* `--uitest` builds the shell and every dialog headlessly and drives the parts that only exist in
  response to input — expansion, search, each sort column, and the staged-edit marker — reporting 22
  checks. It passes against six real archives of different shapes (1 to 215 entries).
* Saving a 578 MB archive peaks at **610 MB** working set (the payloads themselves) rather than the
  1188 MB it needed when the whole output was built in one array, and takes under a second.

### A note on failure behaviour

A save that fails on one archive **keeps that archive's staged edits**, so the cause can be fixed and
the save retried. A load in which every path fails **keeps the current view**, including work staged in
it, rather than clearing it to replace it with nothing. Both are covered by tests, because the first
version of each got this wrong: the failure was reported and then the work was destroyed anyway.

---

## Scope

No texture, model or DAT parsing; pkz members are opaque bytes. No cpk support. No cross-platform
conversion. The tool edits the archives it is given and nothing else. There is no undo history beyond
**Revert** on a row and **Close all** discarding everything.
