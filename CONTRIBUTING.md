# Contributing to Translate Live

Thank you for helping improve Translate Live. Focused bug fixes, tests, documentation, accessibility improvements, and well-scoped features are welcome.

## Before you start

- Search existing issues and discussions before opening a duplicate.
- For a substantial behavior or architecture change, open an issue first and describe the user problem, proposed scope, and validation plan.
- Never include API keys, credential files, classroom transcripts, summaries, settings backups, or personal paths in an issue, commit, screenshot, or test fixture.
- Read `AGENTS.md` and `docs/PROJECT.md` before changing the implementation.

## Development setup

Translate Live requires Windows 11 and the .NET SDK version pinned in `global.json`.

```powershell
git clone https://github.com/zhaopengyi16-prog/Translate-Live.git
cd Translate-Live
./scripts/build.ps1
./scripts/test.ps1
```

Use a temporary `LECTURE_COPILOT_DATA_ROOT` for UI or integration testing. Do not point automated tests at your normal application data.

## Pull requests

1. Keep the change focused and preserve upstream attribution.
2. Add or update tests for behavior changes.
3. Run the build and test scripts locally.
4. Review the final diff for credentials and personal data.
5. Describe the user-visible result, verification evidence, and remaining limitations in the pull request.

The Windows CI workflow must pass before merging. A successful build alone is not proof that Live Captions capture, audio-device selection, overlay behavior, or Windows display scaling works; describe any manual checks you performed.

## 中文说明

欢迎提交范围明确的问题修复、测试、文档、无障碍改进和功能变更。较大的行为或架构调整请先开 Issue 说明用户问题、范围与验证方式。

提交 Issue、截图、测试数据或代码前，必须移除 API 密钥、凭据文件、课堂原文、译文、总结、设置备份和个人路径。开发前请阅读 `AGENTS.md` 与 `docs/PROJECT.md`，并使用隔离的 `LECTURE_COPILOT_DATA_ROOT` 测试，不要操作日常使用的数据目录。

Pull Request 应保持单一目标，保留上游署名，补充必要测试，运行构建与测试脚本，并说明实际验证结果及尚未覆盖的人工验证。
