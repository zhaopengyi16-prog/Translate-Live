# Security Policy

## Supported versions

The project is currently a development preview. Security fixes are made on the `main` branch and will be included in the next release. No older binary release line is currently supported.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting for this repository instead of a public issue. Include:

- the affected commit or version;
- the impact and conditions required to reproduce it;
- minimal reproduction steps using synthetic data;
- any proposed mitigation.

Do not include real API keys, DPAPI credential files, classroom databases, transcripts, summaries, logs containing classroom context, or other personal data. If a credential may have been exposed, revoke or rotate it with the provider before sending a report.

For ordinary bugs without security impact, use the public issue templates.

## 中文说明

本项目目前是开发预览版。安全修复在 `main` 分支完成并进入下一次发布，暂不承诺维护旧版二进制发布线。

安全漏洞请使用本仓库的 GitHub 私密漏洞报告功能，不要发布公开 Issue。报告中请提供受影响提交、影响范围、使用合成数据的最小复现步骤和建议缓解方式。

请勿提交真实 API 密钥、DPAPI 凭据文件、课堂数据库、原文、译文、总结或含课堂上下文的日志。若凭据可能已经泄露，请先在对应服务商处撤销或轮换。
