# Changelog

## [0.3.3] - 2026-09-14

A client built for another platform no longer passes for this one: the launcher looks for the
native library only this platform's package carries (`Lib/e_sqlite3.dll`, `libe_sqlite3.dylib`,
`libe_sqlite3.so`) on Windows as on Linux and macOS, and a client request into a slot holding a
foreign client goes to `<slot>-client` the way a server request already did. The generated launch
configurations give each platform its own client slot, `.game/<series>-linux` and `-macos` in the
checkout and `%LOCALAPPDATA%\exmod\game\<series>` on Windows, where a client on a network share
(a checkout under `\\wsl.localhost`) cannot load its native libraries, so a checkout shared
between Windows and WSL keeps both clients and F5 works from either side. The Windows client's data lives beside it under
`%LOCALAPPDATA%\exmod\data\<repo>`, since SQLite cannot lock a save file over a share. On Linux the client runs on X11: GLFW's Wayland backend cannot place the cursor, which
mouse look needs, so the launcher and the launch configuration hand GLFW a display name no
compositor answers to and it falls back to XWayland. `exmod client -Software` runs the game on
Mesa's software rasterizer for a GPU driver that hangs it.
On Windows the client installer runs into a local temporary folder as a per-user install, which
needs no elevation and works for a checkout on a network share (WSL seen from Windows), and the
tree is copied into the slot; the generated tasks run pwsh with `-ExecutionPolicy Bypass`, so a
script on a share no longer asks before every step.
`stage` and `client` build the mods every time (an incremental build, seconds when nothing
changed) instead of only when nothing was built yet, so F5 never runs a mod older than its source.
A project last built on another platform (a checkout shared between Windows and WSL) is built from
clean, because MSBuild's incremental clean, fed the other platform's file list, deleted the copied
modinfo.json and modicon.png and `stage` then found no mod to stage.

`exlib-verify` now catches the client's own "Missing mapping for texture code" defect headlessly: a
new check reads every shape a blocktype's or itemtype's `shape`/`shapeByType` (alternates included)
names, and reports a face's `#code` that neither the shape's own `textures` nor the definition's
`textures`/`texturesByType` for that variant covers - the `all`/`sides`/`horizontals`/`verticals`
shorthands honoured the same way `exlib-shapes` reads them. A block finding is an error, since the
client always logs one; an item finding is informational, since the client silently leaves the face
untextured there instead.

## [0.3.2] - 2026-09-14

`exlib-shapes item FILE --out DIR [--variant CODE]` renders an itemtype variant: the isometric view
of its own shape, painted with the itemtype's texture map, and its flat inventory texture enlarged
four times on the renders' own paper. `exmod item` runs it.

A picture shows the front of a machine whatever the files say about facing. A structure or
megablock is turned so its starter block stands on the camera-facing edge of the footprint it
reserves - the player works from that side - and every block of it turns with the structure, so the
blast furnace's door and the cowper stove's intake face the reader instead of a blank wall. A block
family with no facing variant to choose between is turned by its own art instead, toward whichever
quarter turn brings the most of its detail to the camera, and `block` takes `--angle` to override
either. Both manifests name the turn and the side the front then looks toward.

A block's views leave out a part its own shape animates away from where the rest pose parks it - the
puddling door's rabble and paddle, the chimney cap's control rod - and `--full` keeps them; the
manifest says which was used and what it left out. Static art keeps its place however far past the
block's own cells it reaches.

A blocktype's texture entries resolve the way the game resolves them: `overlays` are composited over
the base, `baseByType` is read through the ByType rule, and the `all`, `sides`, `horizontals` and
`verticals` shorthands stand in for the faces a shape names one by one, which is what left the
slab-lined furnace cores painted magenta. A face no texture is assigned to is named in the manifest
under `unpaintedFaces`, apart from the `missingTextures` whose value names a file that is not there;
and a hollow model reads as hollow - back faces are drawn, and an opening a view looks straight
through is closed behind with a neutral interior shade instead of showing the paper.

Plan and footprint SVGs are as wide as their own caption, which used to be clipped at both ends on
every megablock page; every drawn cell carries its legend number in ink the fill's own luminance
chooses; an optional cell is outlined and hatched rather than left off the picture the legend
promises it on; the legend's rows are numbered for display in the manifest beside the mod's own
number; and the edges are labelled for the machine - `front` under the near edge, `back` over the
far one - instead of the compass letters `x` and `z`. The isometric composite's layer scale reads on
the corner column nearest the camera, each tick carried across the picture as a faint guide, so a
layer can be counted where a reader is actually looking.
Raster labels are set in a Latin subset of Noto Sans carried inside the tool, so a picture is the same
on every machine whatever fonts it has.

`exlib-shapes block FILE --out DIR [--variant CODE]` renders one blocktype variant the way the game
draws it - its own shape under the turn its `shape`/`shapeByType` entry carries for that variant,
painted with the blocktype's texture map - one PNG per view, the plan of the footprint it reserves,
and a manifest naming the variant, the textures it could not find and its warnings. `exmod block`
runs it.

`shape.rotateYByType` (and the X/Z forms) are read through the game's own ByType rule, which is
where the old mods keep a boiler's or an engine's spin, and a variant group naming only a
worldproperties file reads it from the domain the reference names - `game` when it names none,
which is where `abstract/horizontalorientation` actually lives.

A megablock's schematic now stands its declared footprint in the frame its model is drawn in: the
cells turn onto the body rather than away from it at every facing, a megablock that also declares a
`multiblockStructure` included, its own body is drawn over a thin outline of the cells it reserves
instead of under grey boxes, and a structure's filler cells keep theirs. Every plan SVG carries the
layer it draws as a caption, the manifest lists each plan with its layer under `plans`, and the
isometric composite carries a vertical scale, one tick per layer.

`schematic` and `block` draw an oriented family at its presentation facing. A machine is placed
facing away from the player, so its front - the boiler's firebox, the engine's cylinder end, the
furnace's door - is the side opposite the variant's facing, and the drawing takes the facing that
turns that front toward the isometric camera, which stands to the south-east. `--variant` and
`--angle` still override the choice, the plans keep north up, and both manifests name the side the
front looks toward under `front`.

A first run of a generated repository's launcher no longer prints git's
`refs/tags/v0.3.1 ... is not a commit!`: the clone of the pinned tools takes the default branch
with nothing checked out, and the annotated tag is fetched and checked out after it, the way an
already-cloned `.extools/` moves between pins. The generated README says plain `setup` provisions
the dedicated server only and names what fetches the client the launch configurations point at,
`.game/<series>/Vintagestory.dll`.

The generated `.vscode/tasks.json` runs the launcher itself, `scripts/exmod.sh` through bash and
`scripts/exmod.ps1` through pwsh on Windows, so a task no longer needs pwsh on PATH; the current
series' launch configuration runs the game on the system .NET, and only the legacy series carry
`DOTNET_ROOT` and provision their own. `stage` keeps the build's console lines out of the list of
staged mods it returns, which used to surface as `modinfo.json has no modid to stage under`. A data
folder the game has never written is seeded windowed with vsync off, so a debug session keeps the
editor in reach.

## [0.3.1] - 2026-09-14

A fresh clone works: `setup` used to run the API patcher from inside the tools checkout, where
this repository's own build props demand a game install the consumer's `.extools/` never has,
so the patch failed and every test that mocks a player failed with it; the patcher now runs
from a scratch copy, and a failure is a warning that names the cause. `exmod test` prints each
failing test's name and first message line under its project's FAIL line. `exmod starter` and
`exmod new` write `.vscode/tasks.json` and `.vscode/launch.json` (the stage, pack, test and
launch-prep tasks and one launch configuration per game series, like the family repositories
carry), and the generated README opens with what a new modder needs before the first `setup`.

## [0.3.0] - 2026-09-14

`exmod scaffold` (alias `g`) puts a compiling, tested block, item, recipe, megablock, multiblock,
node, blockbehavior, entitybehavior, config, migration or command into an existing mod, from the
`dotnet new` templates exlib ships as `ExpandedLib.Templates`; the lang keys the generated code
reads are merged into the mod's `en.json`. `exmod starter` now reads its sample list from exlib's
own manifest, in manifest order, and generates every mod in the family layout
(`mods/<id>/{src,assets,tests}`), the same shape a real mod or `exmod new` scaffold uses - the
README's mods paragraph is a table read from each sample's own `modinfo.json`, and a cross-sample
`ProjectReference` (e.g. a mill depending on a grain catalogue) is rewritten generically instead of
by name.

A second .NET tool, `exlib-shapes` (`ExpandedLib.Shapes`, packed alongside `ExpandedLib.Verify`):
renders a shape file to textured views and animation frames, and a multiblock or megablock
blocktype file to a build schematic (a plan-grid SVG per Y layer, an isometric textured composite,
a manifest). Every reference render matches within a small pixel tolerance, except the `iso`
view, where a few hundred pixels on exactly-touching faces flip a strict z-test on a float
rounding difference (documented on `RendererTests.Matches_the_reference_render`). `exmod render`
and `exmod schematic` run it through `dotnet run --project` the way `exmod verify` runs
`exlib-verify`; `--out` needs the `=` form there (`--out=DIR`), since PowerShell's own parameter
binder treats a bare `--out` as an ambiguous prefix of its common `-OutVariable`/`-OutBuffer`
parameters. A shared `assets/ExlibAssets` library (the lenient asset store, the block/item
catalogue, the game install resolver, the mod source) now backs both tools; `verify/ExlibVerify`
carries no copies of its own.

## [0.2.4] - 2026-09-07

The coverage gate finds the floors file where the manifest names it, else at `tests/` or
`infra/test/`, and runs no gate in a repository that has none, instead of failing the test command.

## [0.2.3] - 2026-09-07

The manifest names the coverage floors file (`coverageFloors`, default `infra/test/coverage-floors.json`),
and `format`, `check` and `clean` cover the parents of its `tests` entries, so a repository whose
tests are not under a mod folder is formatted whole. A manifest tests entry runs across every
declared series. A generated tests workflow provisions the dependency mods before the tests; a
generated starter is formatted with the same two passes as `exmod format` and follows exlib's
current samples and layout.

## [0.2.2] - 2026-09-07

A generated tests workflow provisions the dependency mods before the tests, so the harness guards
that read exlib's shipped assets pass in a standalone clone. The starter transform follows exlib's
samples, which now declare `AssetDomain` instead of hand-rolling the asset glob.

## [0.2.1] - 2026-09-07

`format`, `check` and `clean` no longer fail in a repository whose manifest names no samples
(the starter, the family). A generated starter is formatted with the pinned CSharpier before its
first commit.

## [0.2.0] - 2026-09-07

Two commands for a new repository. `exmod starter <dest>` generates the standalone starter
repository from exlib's HelloModule and HelloExpanded samples: the four csproj files and each
modinfo are transformed from the samples' own text (package mode, versions pinned to the exlib
checkout), and the command writes the solution, the launchers, the manifest, the packages props,
CI pinned to a game patch, the dotfiles, an MIT licence template and a README. A re-run
regenerates what it owns and carries forward every mod added since. `exmod new <modid>` scaffolds
an empty mod, or with `--module` an exlib module, into the current repository and registers it in
the manifest and the solution.

## [0.1.2] - 2026-09-07

Output of a build or an archive extraction run inside a helper that returns paths no longer joins
the returned list; the smoke lane on a fresh checkout failed on an empty path where a build had
printed a line. The coverage gate reads the floors file by an explicit path under the repository
root instead of the working directory.

## [0.1.1] - 2026-09-07

A checkout of this repository ends MSBuild's search for Directory.Build.props and .targets, so a
consumer's `.extools/` clone no longer takes that repository's build logic: under exlib, the
publicizer's build inherited the auto-provisioning target and re-entered the provision lock, and
every CI provision hung. The lock wait is bounded at twenty minutes and fails with the cause. The
provisioning web calls time out and retry, and the shared `.game` and `.dotnet` installs are no
longer tracked.

## [0.1.0] - 2026-09-07

The tooling of the Expanded family monorepo in its own repository: the `exmod` dispatcher and
its five stages, the two launchers a consuming repository checks in, the manifest-driven packaging
build, the standalone `exlib-verify` tool, the publicizer, the coverage gate and the released-codes
derivation, and the CI templates. Every repository-specific fact is read from the consuming
repository's `exmod.json`; runtime dependency mods resolve from a workspace sibling or a release.
