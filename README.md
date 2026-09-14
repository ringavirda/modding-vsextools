# extools

The build and release tooling for Vintage Story mods built on Expanded Library: `exmod`, one
CLI for the whole lifecycle of a repository (provision the toolchain and the game, build, test,
format, verify shipped assets, run a client or a server, smoke-boot, package, release), the
packaging build it drives, the `exlib-verify` tool, and the helper scripts behind them.

## Using it from a repository

A consuming repository checks in two files and a pin:

```
scripts/exmod.sh     the POSIX launcher   (copy of wrappers/exmod.sh)
scripts/exmod.ps1    the PowerShell launcher (copy of wrappers/exmod.ps1)
exmod.json           "tools": "<version>" plus what the repository holds
```

Both launchers find this checkout in the same order and stop at the first that holds `exmod.ps1`:
the `EXTOOLS_HOME` environment variable; the workspace sibling `../extools`; a clone of the pinned
tag under `.extools/` (made on first use, moved when the pin changes; `EXTOOLS_URL` overrides the
clone source). They then run the dispatcher with the repository as its root. `bash scripts/exmod.sh`
lists every command; `exmod help <command>` describes one. The repository's `exmod.json` names its
mods, samples, test projects, packable projects, game series and dependencies; the field list is in
the Expanded Library wiki under Getting Started, "exmod in your repo".

## Layout

```
exmod.ps1        the dispatcher: argument helpers, the manifest resolvers, command registration, help
exmod/           one file per lifecycle stage: provision, new, scaffold, src, run, dist, windows
wrappers/        the two launchers a consuming repository checks in
scripts/         the launchers again, pointed at this checkout, so extools drives itself
pack/            the packaging build (Cake Frosting), manifest-driven
verify/          exlib-verify, a .NET tool that checks a mod's shipped assets with no game running
tools/           the API publicizer provisioning applies, the coverage gate, the released-codes derivation
templates/ci/    GitHub Actions templates for a consuming repository
```

## Starter

`exmod starter <dest>` generates a standalone starter monorepo at `<dest>`: every sample exlib's own
`exmod.json` names, in manifest order, as its own mod (`mods/grains`, `mods/handmill`, and whatever
else exlib ships samples for), their tests, a solution, the launcher scripts, an `exmod.json` naming
them, a `Directory.Packages.props` pinned to the exlib version it was generated from, CI and the
repo dotfiles - a repository that clones, restores from NuGet, builds, tests and smokes with nothing
hand-edited. `-ExlibRoot` points it at an exlib checkout other than the workspace sibling `../exlib`;
`-Version` pins a different `ExpandedLib` version than that checkout's own; `-Force` allows
generating into a non-empty `<dest>` that carries no marker of a previous run. Each mod/test csproj
and `modinfo.json` is generated from the matching sample's own files by a text transform, never a
hand-maintained template, so they cannot drift from what exlib's own gate already proves; the
solution, `Directory.Packages.props`, CI, the dotfiles and the README are written by the command
itself. Re-running it over an existing `<dest>` overwrites every path it owns and carries forward
any mod `exmod new` has added since into the fresh manifest and solution; `git status --porcelain`
in `<dest>` afterward reports what changed, including any of the owner's own unrelated edits.

`exmod new <modid>` scaffolds an empty mod into the current repository (the one its own `exmod.json`
names) - a package-mode csproj under `mods/<modid>/src`, `modinfo.json`, an asset skeleton and a
test project wired to the harness - and adds it to `exmod.json` and, when the repository names a
solution, to it too. `--module` scaffolds a framework module instead, the shape `samples/Grains` and
the starter both carry: `[assembly: ExModule]`, an `IExModule` entry point and the empty `ModSystem`
the engine's Code-mod loader requires. It needs a root `Directory.Packages.props` carrying an
`ExpandedLib` `PackageVersion` to pin against, and always scaffolds a versionless, package-mode
`PackageReference` - the shape `exmod setup` produces, never a workspace `ProjectReference`. A
generated starter's own mods sit in the same family layout (`mods/<id>/src/<Name>.csproj`,
`modinfo.json` beside it), since they are generated from the samples directly.

`exmod scaffold <kind> <Name> [-Mod <id>]` (alias `g`) puts a compiling, tested `<kind>` - a block,
item, recipe, megablock, multiblock, node, blockbehavior, entitybehavior, config, migration or
command - into an existing mod, from the `dotnet new` templates exlib ships as
`ExpandedLib.Templates`: the sibling exlib checkout's own `templates/content/` in source mode, else
the version `Directory.Packages.props` pins. Lang keys the generated code reads are merged into
`assets/<id>/lang/en.json`.

## Branches

Day-to-day work lands on `dev`. `main` moves by fast-forward when a batch is green locally, and
that push is what runs CI; tags are cut from `main`. Push `dev` freely, it runs nothing.

## Releasing

Bump `"tools"` in `exmod.json` (it is also `exlib-verify`'s package version), note the change in
`CHANGELOG.md`, tag `v<version>`, push the tag. A consuming repository moves by editing its own
`"tools"` pin.

## Developing

`bash scripts/exmod.sh test latest` builds and tests the verify tool against a provisioned game
install (`bash scripts/exmod.sh provision game -Kind server` fetches the dedicated-server archive,
which needs no licence). The scripts are PowerShell 7; `exmod.sh` installs `pwsh` into
`.dotnet/tools` when the machine has none.

MIT licensed.
