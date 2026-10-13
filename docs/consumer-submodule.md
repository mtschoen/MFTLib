This recipe will be retired with the consumer bridges tracked by [file-wizard issue 288 (NuGet bridge retirement)](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/288) and [git-wizard issue 134 (NuGet bridge retirement)](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/134); until then it is the current mechanism, documented below.

# Consumer submodule and CI recipe

Consumers of MFTLib (such as `file-wizard` and `git-wizard`) build it from source through a git submodule:

1. **Submodule convention**: Declare MFTLib as a submodule whose url resolves to `https://gitea.fleet.sticktoitive.net/schoen/MFTLib.git`. Both consumers declare it at `external/MFTLib` with the relative url `../MFTLib.git`, which keeps the submodule on the same Gitea instance and under the same owner as the consumer. The gitlink is the pin: the commit sha recorded at that path is the MFTLib revision the consumer builds, and it is the only place that revision is stored.
2. **Fan-out by hand**: the pin moves in each consumer's own pull request, because a breaking MFTLib change leaves an automatic pin-bump pull request unable to compile. `.gitea/workflows/sync-consumers.yml` runs on `workflow_dispatch` only: when run by hand it executes `scripts/sync_consumers.sh`, enumerates `schoen/*` repos on Gitea, and opens a `chore/mftlib-pin-bump` pull request as the `claude-code` bot (backed by the `MFTLIB_SYNC_TOKEN` Actions secret) in every repo whose `.gitmodules` declares a submodule resolving to MFTLib. That pull request commits the new sha into the gitlink. The submodule path is read from `.gitmodules` rather than assumed. A repo with no such submodule is not a consumer and is skipped.
3. **A silent no-op is a failure**: a fan-out that matched zero consumers exits non-zero instead of reporting success, and so does one where any single consumer failed to bump. A green run that updated nothing is what let both consumers drift four MFTLib pull requests behind ([MFTLib issue 194 (consumer pin drift)](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/194)).
4. **Local development**: Populate the submodule with `git submodule update --init --recursive`. Do this after a `git clean -ffxd`, which removes the checked-out submodule content along with every other untracked file.

### Consumer CI recipe

Check the submodule out as part of the build instead of cloning MFTLib separately, so the revision CI builds is exactly the gitlink the repository pins:

```yaml
- uses: actions/checkout@v4
  with:
    submodules: recursive
```

MFTLib source then sits at `external/MFTLib`. Build the native core with the toolchain that can compile `MFTLibNative.vcxproj`, and the managed assemblies with `dotnet`.

#### Bash (Linux CI)

```bash
# Native core, plus its smoke test, driven by MFTLib's own Linux build script
# (cmake + Ninja into external/MFTLib/build/linux, which MFTLib gitignores).
bash external/MFTLib/scripts/build-linux.sh

# Managed assemblies
dotnet build external/MFTLib/MFTLib/MFTLib.csproj -c Release -p:Platform=x64
dotnet build external/MFTLib/MFTLibTestExtensions/MFTLibTestExtensions.csproj -c Release -p:Platform=x64
```

#### PowerShell (Windows CI)

```powershell
# MFTLib's own script owns the Windows recipe: it restores, locates the amd64
# Visual Studio MSBuild (the only toolchain that can compile the native C++
# vcxproj, and the one a host-mode runner needs to avoid WOW64 redirection),
# builds the native project with an absolute trailing-backslash SolutionDir so
# its output lands in the submodule's root x64\Release (the only place the
# managed projects look for MFTLibNative.dll), and then builds the managed projects.
.\external\MFTLib\scripts\build-windows.ps1
```
