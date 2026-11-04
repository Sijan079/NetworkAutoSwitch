# Contributing

Thank you for looking at Network Auto Switch. This file is the short version of how to send a change.

## Before you start

- **Small fixes**: just open a pull request.
- **New features or behaviour changes**: open an issue first and describe what you want and why, so we agree before you spend time on it.
- Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (where things live) and [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) (build, tests, conventions).

## A good change

1. **Fork**, create a branch, and make the change.
2. **Keep logic testable.** Put decisions in the pure parts of `src/Core` and add tests for them in `src/Cli/Program.cs`.
3. **Run the checks**; both must pass:
   ```powershell
   dotnet build -c Release
   dotnet run -c Release --project src\Cli -- --selftest
   dotnet run -c Release --project src\Cli -- --engine-test
   ```
4. **UI changes**: render both themes with `NetworkAutoSwitchTray.exe --dry-run --snapshot <folder> --theme dark` (and `light`) and look at the result.
5. **Describe the change for users** under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md) (*Added*, *Changed*, *Fixed*, *Removed*; written for people who use the app, not for developers).
6. Open the pull request. CI builds it and runs the unit tests.

## Rules that protect users

- The app must **always be able to put the network back** (exit, pause, crash, uninstall). Never add a change to the network that `state.json` and `--restore` cannot undo.
- **Never delete user data without asking.**
- Old `policy.json` and `settings.json` files must keep loading: new fields are optional with defaults.
- Do not change the installer's `AppId`, and do not edit the version anywhere except `Directory.Build.props` (see [docs/RELEASING.md](docs/RELEASING.md)). Releases are made by the maintainer.

## Reporting a problem

Open an issue with: your Windows version, what your two connections are (Wi-Fi, phone tether, VPN), what you expected, what happened, and the last lines of the log
(`C:\ProgramData\NetworkAutoSwitch\log.txt`; the Log page can copy them). Please leave out anything private.

## License

By contributing you agree that your change is released under the project's [MIT License](LICENSE).
