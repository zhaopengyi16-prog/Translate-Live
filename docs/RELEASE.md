# Translate Live build and release guide

Last updated: 2026-09-11

## Release status

The repository is ready for public source review. It does not yet claim a signed or generally available binary release.

Current public version: `0.1.0.0`

Verified local baseline:

- Windows x64;
- .NET SDK 10.0.400;
- locked dependency restoration;
- Release build successful;
- 105 unit tests passing;
- self-contained win-x64 publish successful;
- Windows Live Captions system-audio and microphone smoke checks successful in an isolated data root.

## Supported release target

The first supported binary target is `win-x64`. The project declares `win-arm64`, but ARM64 must not be advertised as verified until it has an independent build and desktop integration run.

## Required toolchain

- Windows 11
- PowerShell 7 or Windows PowerShell
- .NET SDK 10.0.400
- Windows Live Captions and at least one installed speech-recognition language

The SDK version is fixed by `global.json`. Dependencies are fixed by the root and test-project `packages.lock.json` files.

## Standard verification

Run from the repository root:

~~~powershell
./scripts/build.ps1
./scripts/test.ps1
./scripts/publish-dev.ps1
~~~

Equivalent core commands:

~~~powershell
dotnet restore LiveCaptionsTranslator.sln --locked-mode
dotnet build LiveCaptionsTranslator.sln -c Release --no-restore -m:1 -nodeReuse:false
dotnet test LiveCaptionsTranslator.sln -c Release --no-build --no-restore -m:1 -nodeReuse:false
dotnet publish LiveCaptionsTranslator.csproj -c Release -r win-x64 --self-contained true --no-restore -m:1 -nodeReuse:false -p:PublishSingleFile=false -o artifacts/dev-win-x64
~~~

The current self-contained output is intentionally multi-file. Do not silently switch to single-file publishing without rechecking WPF startup, native SQLite loading, UI Automation, and Windows Live Captions behavior.

## Windows integration checks

Compilation is not sufficient evidence for the capture path. Before a binary release:

1. Set `LECTURE_COPILOT_DATA_ROOT` to a new temporary directory.
2. Start the published executable in a normal interactive Windows desktop session.
3. Verify Windows Live Captions reaches the **working** state.
4. Play a fixed, non-personal audio sentence and confirm a caption node becomes available.
5. Verify microphone mode changes the Live Captions microphone state from Off to On.
6. End the class and confirm one isolated session is saved.
7. Close Translate Live and confirm no app-owned Live Captions process remains.
8. Confirm the production data directory was not modified.

The smoke-test project reports only state, availability, and character counts; it must not print captured content.

## Security and privacy gate

Before every public push or release:

- inspect `git status` and the exact staged file list;
- verify that `Data/`, `Logs/`, `Backups/`, `Recovery/`, `.tools/`, build outputs, and databases are ignored;
- scan staged files for provider-key patterns, private keys, tokens, and connection strings;
- confirm no settings, DPAPI credential blobs, SQLite databases, JSONL logs, crash dumps, or exports are staged;
- inspect screenshots for names, course content, account details, and other personal data;
- confirm every bundled image, font, and icon has redistribution permission;
- build and test from a clean clone of the exact commit to be pushed.

Never publish a real API key even if it has already been revoked. Do not use classroom recordings or transcripts as release fixtures.

## Packaging

The developer publish script writes:

`artifacts/dev-win-x64/LectureCopilot.Dev.exe`

The internal filename remains `LectureCopilot.Dev.exe` to preserve compatibility with:

- `%LOCALAPPDATA%\LectureCopilot.Dev`;
- existing DPAPI-protected credentials;
- the single-instance mutex;
- saved settings and classroom history.

The user-facing product name remains **Translate Live**.

## Versioning

Use four-part assembly versions and matching Git tags:

`vMAJOR.MINOR.PATCH.REVISION`

For the initial public preview, use the `0.1.x.x` line. Update both `Properties/AssemblyInfo.cs` and `Properties/AssemblyInfo.tt` in the same change.

## GitHub release checklist

- [ ] Clean GitHub Actions run on the release commit
- [ ] win-x64 clean-clone build and tests
- [ ] Windows Live Captions computer-audio smoke test
- [ ] Windows Live Captions microphone smoke test
- [ ] isolated settings, credential migration, and corrupted-ciphertext tests
- [ ] no sensitive or personal files in the Git tree
- [ ] third-party license and attribution review
- [ ] release notes describing data migrations and known limitations
- [ ] SHA-256 checksum for every binary archive
- [ ] code-signing status stated clearly

## Known blockers for a stable binary release

- no code-signing certificate or installer;
- no independently verified ARM64 integration run;
- Windows Live Captions UI Automation may change across Windows updates;
- executable and data-directory renaming requires a deliberate compatibility migration;
- GitHub release automation should be added only after the packaging names and signing process are settled.

## Upstream attribution

Translate Live is derived from [SakiRinn/LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator) and retains the Apache License 2.0 license and upstream author attribution. Translate Live modifications and maintenance are credited to QuBe.
