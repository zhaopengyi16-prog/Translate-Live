# Translate Live

面向 Windows 11 的实时双语字幕、课堂翻译、课程回顾与总结工具。

[English](README.md) · [项目说明](docs/PROJECT.md) · [构建与发布](docs/RELEASE.md) · [贡献指南](CONTRIBUTING.md) · [安全策略](SECURITY.md)

[![Windows CI](https://github.com/zhaopengyi16-prog/Translate-Live/actions/workflows/dotnet-build.yml/badge.svg)](https://github.com/zhaopengyi16-prog/Translate-Live/actions/workflows/dotnet-build.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4.svg)](https://support.microsoft.com/zh-cn/windows/使用实时字幕更好地理解音频-b52da59c-14b8-4031-aeeb-f6a47e6055df)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)

> [!NOTE]
> 当前仓库是可公开审查的开发预览版。源码构建已经可用；代码签名、安装程序和稳定版 GitHub Release 仍在规划中。

## 它能做什么

Translate Live 将 Windows 实时字幕扩展成面向课堂的双语工作台：

- 在线课堂捕捉电脑声音，线下课堂按用户操作启用麦克风；
- 连续显示原文与译文，处理草稿修订并保持字幕顺序；
- 按“开始到结束”将每次课堂保存为独立会话；
- 支持课程及逐句记录的搜索、编辑、删除和 CSV 导出；
- 使用独立模型生成、复制并保存课堂总结；
- 提供可缩放、可置顶、可穿透的悬浮字幕；
- 使用 Windows DPAPI 保存服务商凭据，不把密钥写回普通设置文件。

## 支持的翻译引擎

| 引擎 | 配置方式 |
|---|---|
| Google / Google2 | 内置公开端点；Google2 可选环境变量密钥 |
| OpenAI 兼容接口 | 自定义地址、模型、提示词与密钥；兼容 DeepSeek 流式响应 |
| OpenRouter | API 密钥与模型 |
| Ollama | 本地或自托管端点 |
| DeepL | API 密钥与端点 |
| 有道 | App Key 与 App Secret |
| 百度 | App ID 与 App Secret |
| MTranServer | 自托管端点与可选密钥 |
| LibreTranslate | 自托管端点与可选密钥 |

课堂总结模型独立配置，避免长文本总结任务占用实时翻译设置。

## 系统要求

- Windows 11 22H2 或更高版本，并支持 Windows 实时字幕。
- 已安装与课堂语音一致的 Windows 语音识别语言包。
- 云端翻译服务需要网络；Ollama、MTranServer 与 LibreTranslate 可自行部署。
- 从源码构建需要 .NET SDK 10.0.400，版本由 `global.json` 固定。

## 从源码构建

~~~powershell
git clone https://github.com/zhaopengyi16-prog/Translate-Live.git
cd Translate-Live

./scripts/build.ps1
./scripts/test.ps1
./scripts/publish-dev.ps1
~~~

自包含 Windows x64 产物位于：

`artifacts/dev-win-x64/LectureCopilot.Dev.exe`

为了兼容已有设置、DPAPI 凭据和课堂记录，内部可执行文件名及数据目录暂时保留 `LectureCopilot.Dev`；用户界面显示名称为 **Translate Live**。

## 第一次使用

1. 首先开启一次 Windows 实时字幕，安装所需识别语言。
2. 启动 Translate Live。
3. 在“设置”中选择翻译引擎和目标语言。
4. 选择“在线课程 · 电脑声音”或“线下课堂 · 麦克风”。
5. 结束课堂后，在“课堂回顾”中搜索、编辑、删除或导出记录。

如果字幕区持续为空，请先确认 Windows 实时字幕的源语言正确，并确认顶部状态已经从“Live Captions 已就绪”变为“Live Captions 正在工作”。

## 数据与隐私

仓库和发布物不包含服务商密钥或课堂内容。运行数据保存在：

`%LOCALAPPDATA%\LectureCopilot.Dev`

| 数据 | 位置 | 保护方式 |
|---|---|---|
| 普通设置 | `Data/setting.json` | 不含服务商密钥的 JSON |
| 服务商凭据 | `Data/credentials.dat` | Windows DPAPI `CurrentUser` |
| 字幕、译文和总结 | `Data/translation_history.db` | 本地 SQLite 数据库 |
| 诊断日志 | `Logs/app-*.jsonl` | 仅事件名、异常类型、HRESULT 和耗时 |
| 备份与恢复状态 | `Backups/`、`Recovery/` | 本地应用数据 |

声音转文字由 Windows 实时字幕处理。识别后的文字只发送给用户选择的翻译或总结服务商。提交公开 Issue 时，请勿附带真实密钥、设置文件、课堂数据库或包含敏感上下文的日志。

## 工作原理

~~~text
电脑声音 / 麦克风
        |
Windows 实时字幕
        |
UI Automation 字幕捕捉
        |
字幕稳定化与修订判定
        |
翻译队列 --------> 用户选择的翻译服务
        |                    |
        +---- 主界面/悬浮窗 <-+
        |
SQLite 课堂会话与回顾
        |
独立课堂总结模型
~~~

模块边界与数据规则见 [docs/PROJECT.md](docs/PROJECT.md)。

## 开发约定

- 使用已提交的 `packages.lock.json` 进行锁定依赖还原。
- WPF 构建保持串行：`-m:1 -nodeReuse:false`。
- UI 和系统集成测试必须设置隔离的 `LECTURE_COPILOT_DATA_ROOT`。
- 禁止提交真实密钥、设置、日志、备份或课堂数据库。
- 自动化修改前请先阅读 [AGENTS.md](AGENTS.md)。

## 当前限制

- 语音识别准确率取决于 Windows 实时字幕、识别语言、音频设备和环境噪声。
- Windows 更新可能改变 Live Captions 的 UI Automation 结构。
- 当前公开构建尚未进行代码签名，可能触发 Microsoft Defender SmartScreen。
- 当前公开基线主要验证 Windows x64；ARM64 已配置，但需要独立发布验收。

## 路线图

- 可重复的 GitHub Release、代码签名和安装程序；
- 可复现并签名的 Windows 发布产物；
- 完善首次使用诊断和服务商健康状态；
- 评估可插拔独立 ASR，并保留 Windows 实时字幕作为回退。

## 参与贡献

欢迎提交问题和范围明确的 Pull Request。提交前请：

1. 删除密钥和个人课堂内容；
2. 提供 Windows 版本、音源模式、服务商名称和可复现步骤；
3. 修改代码后运行 `./scripts/build.ps1` 与 `./scripts/test.ps1`。

完整流程见 [CONTRIBUTING.md](CONTRIBUTING.md)。安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。

## 上游来源与署名

Translate Live 基于 [SakiRinn/LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator) 开发，当前上游发布线包含 `v1.7.1300.1822`。应用界面和源码元数据继续保留原作者与贡献者署名。

Translate Live 二次开发与维护：**QuBe**。

## 许可证

本项目使用 [Apache License 2.0](LICENSE)。上游项目的版权与署名声明继续保留。
