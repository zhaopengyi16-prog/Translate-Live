# Translate Live

Real-time bilingual captions, translation, lecture review, and summaries for Windows 11.

[简体中文](README_zh-CN.md) · [Project notes](docs/PROJECT.md) · [Build and release](docs/RELEASE.md)

[![Windows CI](https://github.com/zhaopengyi16-prog/Translate-Live/actions/workflows/dotnet-build.yml/badge.svg)](https://github.com/zhaopengyi16-prog/Translate-Live/actions/workflows/dotnet-build.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4.svg)](https://support.microsoft.com/windows/use-live-captions-to-better-understand-audio-b52da59c-14b8-4031-aeeb-f6a47e6055df)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)

> [!NOTE]
> This repository is an open-source development preview. Source builds are available now; a signed installer and stable GitHub Release are still planned.

## What it does

Translate Live turns Windows Live Captions into a classroom-oriented bilingual workspace:

- captures computer audio for online classes or microphone audio for in-person classes;
- displays continuously updated source text and translations;
- keeps each class as a separate local session;
- provides searchable, editable records and CSV export;
- generates a class summary with an independently configured model;
- offers a resizable, always-on-top subtitle overlay;
- stores provider credentials with Windows DPAPI instead of plain-text settings.

## Translation engines

| Engine | Configuration |
|---|---|
| Google / Google2 | Built-in public endpoint; Google2 can use an optional environment key |
| OpenAI-compatible | Custom URL, model, prompt, and API key; supports DeepSeek-compatible streaming |
| OpenRouter | API key and model |
| Ollama | Local/self-hosted endpoint |
| DeepL | API key and endpoint |
| Youdao | App key and secret |
| Baidu | App ID and secret |
| MTranServer | Self-hosted endpoint and optional key |
| LibreTranslate | Self-hosted endpoint and optional key |

The summary model is configured separately so long-running summary requests do not share the real-time translation setup.

## Requirements

- Windows 11 22H2 or later with Windows Live Captions.
- A downloaded Windows speech-recognition language pack matching the spoken language.
- Internet access for cloud translation providers. Ollama, MTranServer, and LibreTranslate may be self-hosted.
- .NET SDK 10.0.400 to build from source. The SDK is pinned by `global.json`.

## Build from source

~~~powershell
git clone https://github.com/zhaopengyi16-prog/Translate-Live.git
cd Translate-Live

./scripts/build.ps1
./scripts/test.ps1
./scripts/publish-dev.ps1
~~~

The self-contained Windows x64 build is written to:

`artifacts/dev-win-x64/LectureCopilot.Dev.exe`

The internal executable and data-directory names remain `LectureCopilot.Dev` for compatibility with existing settings, DPAPI credentials, and classroom records. The product name shown in the UI is **Translate Live**.

## First run

1. Enable Windows Live Captions once and install the required recognition language.
2. Start Translate Live.
3. Open **Settings** and select the translation engine and target language.
4. Choose **Online class · computer audio** or **In-person class · microphone**.
5. End the class to finalize the saved session, then open **Class review** to search, edit, delete, or export it.

If captions remain empty, confirm that Windows Live Captions is using the correct source language and that Translate Live shows **Live Captions working**, not only **ready**.

## Data and privacy

Translate Live does not bundle provider credentials or classroom content. Runtime data remains under:

`%LOCALAPPDATA%\LectureCopilot.Dev`

| Data | Location | Protection |
|---|---|---|
| Ordinary settings | `Data/setting.json` | Plain JSON without provider secrets |
| Provider credentials | `Data/credentials.dat` | Windows DPAPI, `CurrentUser` |
| Class transcripts and summaries | `Data/translation_history.db` | Local SQLite database |
| Diagnostics | `Logs/app-*.jsonl` | Event names, exception types, HRESULTs, and durations only |
| Backups and recovery state | `Backups/`, `Recovery/` | Local application data |

Audio-to-text is performed by Windows Live Captions. Recognized text is sent only to the translation or summary provider selected by the user. Do not attach settings, credential files, databases, or logs containing sensitive context to public issues.

## Architecture

~~~text
Windows audio / microphone
        |
Windows Live Captions
        |
UI Automation capture
        |
Caption stabilization and revision policy
        |
Translation queue -----> selected translation provider
        |                         |
        +---- WPF UI / overlay <--+
        |
SQLite class sessions and review
        |
Independent summary model
~~~

See [docs/PROJECT.md](docs/PROJECT.md) for component boundaries and storage rules.

## Development

- Keep dependency restoration locked with the committed `packages.lock.json` files.
- Run WPF builds serially: `-m:1 -nodeReuse:false`.
- Use an isolated `LECTURE_COPILOT_DATA_ROOT` for automated UI and integration checks.
- Never commit real API keys, settings, logs, backups, or classroom databases.
- Read [AGENTS.md](AGENTS.md) before making automated changes.

## Current limitations

- Speech-recognition accuracy depends on Windows Live Captions, its source language, the audio device, and background noise.
- Windows Live Captions UI Automation can change across Windows versions.
- Public binaries are not yet code-signed and may trigger Microsoft Defender SmartScreen.
- The current open-source baseline targets Windows x64 verification; ARM64 is configured but requires independent release validation.

## Roadmap

- signed installer and reproducible GitHub Release;
- CI validation on a clean Windows runner;
- first-run diagnostics and clearer provider health reporting;
- optional pluggable ASR source while retaining Windows Live Captions as a fallback.

## Contributing

Bug reports and focused pull requests are welcome. Before opening an issue:

1. remove API keys and personal transcript content;
2. include the Windows version, capture mode, provider name, and reproducible steps;
3. run `./scripts/build.ps1` and `./scripts/test.ps1` when changing code.

## Upstream and attribution

Translate Live is derived from [SakiRinn/LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator), whose current upstream release line includes `v1.7.1300.1822`. The original authors and contributors remain credited in the application and source metadata.

Translate Live modifications and maintenance: **QuBe**.

## License

Licensed under the [Apache License 2.0](LICENSE). Copyright and attribution notices from the upstream project are retained.
