# Changelog

## [0.3.0] - 2026-09-14

A second .NET tool, `exlib-shapes` (`ExpandedLib.Shapes`, packed alongside `ExpandedLib.Verify`):
renders a shape file to textured views and animation frames, and a multiblock or megablock
blocktype file to a build schematic (a plan-grid SVG per Y layer, an isometric textured composite,
a manifest), matching the Python `vsshape` toolkit's own renderer to within a small pixel
tolerance on every fixture but the `iso` view, where a handful of exactly-touching faces flip a
strict z-test on a BLAS-rounding difference from the Python's numpy matmul (documented on
`RendererTests.Matches_the_reference_render`). `exmod render`
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
