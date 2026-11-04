# Releasing and versioning

## The version number

Network Auto Switch uses **[Semantic Versioning 2.0.0](https://semver.org)**: `MAJOR.MINOR.PATCH`, optionally followed by a prerelease tag.

```
0.4.2           a normal release
0.5.0-beta.1    a prerelease of 0.5.0
1.0.0-rc.2      a release candidate
```

**One place holds the version: `Version` in `Directory.Build.props`.** Nothing else is typed by hand. From it come the program's file version,
the version shown in the window title and the tray menu, the first line of every log session, the installer's file name and its version in
*Apps and features*, and the check that the git tag is right. Never edit a version anywhere else.

### Which number to raise

Ask: *could this change break something for a person who already uses the app?*

| Raise | When | Examples |
|---|---|---|
| **PATCH** (`0.4.2` to `0.4.3`) | Bug fixes and polish. Nothing new to learn, nothing to migrate. | A crash fixed; a label corrected; a faster picker. |
| **MINOR** (`0.4.2` to `0.5.0`) | New things that old setups keep working with. New optional fields in the policy or settings. | A new rule type; a new button; better adapter detection. |
| **MAJOR** (`0.4.2` to `1.0.0`) | Anything that can break an existing setup. | A policy or settings file that old versions cannot be read under; a removed feature or command; dropping Windows 10; a changed install location that needs manual steps. |

Before 1.0.0 (the `0.x` versions) the app is still settling, so a MINOR bump may contain a breaking change, but it must be listed under **Changed** or **Removed**
in the changelog. Version 1.0.0 means: the policy file, the settings file and the installer behaviour are stable, and breaking them needs a MAJOR bump.

### What users can rely on (in every version)

- **Old `policy.json` and `settings.json` files keep loading.** Unknown fields are ignored and missing fields get defaults. A change that cannot keep this promise is a MAJOR change.
- **User data is never deleted without asking.** Upgrading keeps it; uninstalling asks (and the default answer is to keep it).
- **The network is put back.** Exiting, pausing, upgrading and uninstalling all restore the original network settings first.

### Prereleases

Use `-alpha.N`, `-beta.N` or `-rc.N` (counting up from 1) to let people try something before it is final.
A prerelease is **lower** than the same number without the tag (`1.0.0-rc.1` is older than `1.0.0`), is published on GitHub as a *pre-release*,
and is never offered as the latest release. Tags such as `-beta` without a number, or with a `v` inside, are not valid.

## The installer

- It is built with Inno Setup 6 from `installer/NetworkAutoSwitch.iss`. The version is passed in on the command line, never written in the file.
- **The application ID inside the script must never change.** It is how Windows knows a new installer is the same program, so a new version upgrades the old one in place.
- A running copy is asked to exit the normal way first (so it restores the network), then replaced.
- **It never goes backwards**: if a newer version is installed, an older installer stops with a message. Installing the same version again repairs it.
  The comparison uses the SemVer rules above, so `0.10.0` is newer than `0.9.0`, and `1.0.0-rc.1` is older than `1.0.0`.
- Uninstalling restores the network settings, removes the sign-in task, and asks whether to keep your settings, policy and log.
- The app is **self-contained**, so users do not need to install .NET.

## How to make a release

1. **Pick the next version** using the table above.
2. **Update `Version`** in `Directory.Build.props`.
3. **Update `CHANGELOG.md`**: rename `## [Unreleased]` to `## [x.y.z] - YYYY-MM-DD` (and add a fresh empty `## [Unreleased]` above it).
   Group entries under *Added*, *Changed*, *Fixed*, *Removed*. Write for users, not developers.
4. **Check it locally** (see below): the unit tests and the engine test must pass.
5. **Commit** as `Release x.y.z`, and push to `main`. The CI workflow must be green.
6. **Tag and push the tag**: `git tag vX.Y.Z` then `git push origin vX.Y.Z`.
7. The **Release workflow** then runs by itself: it checks the tag matches `Directory.Build.props`, runs the tests, builds the installer and publishes the
   GitHub release with `NetworkAutoSwitch-Setup-X.Y.Z.exe`, its SHA-256 file, and the changelog section as the release notes.
8. **Try the published installer** on a PC (and, for anything but the first release, install it over the previous version).

If the workflow fails, fix the cause and move the tag (`git tag -f`, `git push -f origin vX.Y.Z`) only if no release was created. If a release was created and is wrong,
do not reuse the number: fix it and release the next PATCH.

### Trying a release first

For a risky release, tag a prerelease first (`Version` = `0.2.0-rc.1`, a `## [0.2.0-rc.1]` changelog section, tag `v0.2.0-rc.1`). The workflow publishes it as a **pre-release**,
which is never offered as the latest release. When it checks out, repeat the steps with the final number and delete the prerelease (`gh release delete v0.2.0-rc.1 --cleanup-tag`).

## Building by hand

Needs the .NET 9 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
dotnet build -c Release
dotnet run -c Release --project src\Cli -- --selftest        # unit tests
dotnet run -c Release --project src\Cli -- --engine-test     # the real engine in dry-run (about 15 s)

dotnet publish src\Tray -c Release -r win-x64 --self-contained true -p:DebugType=none -o dist\publish
$v = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" "/DAppVersion=$v" installer\NetworkAutoSwitch.iss
# result: dist\installer\NetworkAutoSwitch-Setup-<version>.exe
```

Adding `/DTestInstall` builds a variant meant for automated checks: it installs for the current user only, creates no sign-in task and never touches a running copy of the app.
`/DPublishDir=<folder>` points it at a different payload, which makes test installers for many versions quick to build.
