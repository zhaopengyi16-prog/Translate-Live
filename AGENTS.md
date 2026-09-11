# Translate Live 工程协作规则

本文件适用于仓库根目录及全部子目录。开始修改前先阅读 `README_zh-CN.md`、`docs/PROJECT.md` 和 `docs/RELEASE.md`。

## 产品与范围

- 本项目是基于 `SakiRinn/LiveCaptions-Translator` 的 Translate Live 开源分支。
- 当前阶段以 Windows 桌面产品的可重复构建、数据安全和可发布性为优先。
- 除非任务明确要求，不进行大规模重构、框架迁移或用户数据格式迁移。

## 数据与密钥

- 用户数据位于 `%LOCALAPPDATA%\LectureCopilot.Dev`，包括设置、SQLite 课堂记录、日志、备份和恢复标记。
- 不读取或输出课堂正文与密钥原文；诊断时仅报告文件、字段、数量和问题类别。
- 禁止将真实 API 密钥、`setting.json`、SQLite 数据库、日志、备份或恢复文件提交到 Git。
- 不删除或覆盖用户设置和课堂数据。涉及迁移时必须先有可验证备份和回滚方案。
- 需要持久化的提供商凭据必须与普通设置分离，写入 `Data\credentials.dat`，并使用 Windows DPAPI `CurrentUser` 保护；不得增加明文回退路径。
- 旧明文设置迁移必须先完成受保护存储的落盘和读回校验，再逐个脱敏应用管理的设置及备份。任一步失败时保留可恢复来源并提示用户，不得静默清空。
- `credentials.dat` 只对创建它的 Windows 用户上下文有效，不得在账户之间复制后假定可解密。测试可注入隔离的测试保护器，生产路径不得替换 DPAPI。
- 诊断日志不得记录异常消息、请求头、请求/响应正文或凭据值；只允许记录事件名、异常类型和 HResult 等无内容元数据。
- Google2 的可选密钥只允许通过 `LECTURE_COPILOT_GOOGLE_TRANSLATE_API_KEY` 环境变量注入；不得再次写入源码。

## 构建与验证

- SDK 版本由 `global.json` 固定为 .NET SDK 10.0.400。
- 推荐依次运行：`scripts/build.ps1`、`scripts/test.ps1`、`scripts/publish-dev.ps1`。
- 依赖还原必须使用锁文件；不得删除或绕过 `packages.lock.json`。
- `bin/`、`obj/`、`artifacts/` 和 `.tools/` 均为生成或本地工具目录，不直接编辑、不提交。
- 不要在同一工作副本中并行运行多个 WPF 构建。若 `MarkupCompile.cache` 被占用，先等待现有 `dotnet/MSBuild` 退出，再重试；不得通过全盘放宽 ACL 或长期管理员运行解决。
- 代码变更至少通过 Release 构建和单元测试；发布相关变更还需完成 `win-x64` 自包含发布。
- 实时翻译链路不得在结果返回后增加固定长等待；草稿可以合并或被同一句最终版本取消，但不同最终句不得静默丢弃、乱序持久化或通过无界并发冲击提供商。
- 网络翻译、界面显示和 SQLite 落盘应保持可取消且边界清晰。调整队列时必须用可控延迟测试覆盖旧草稿抢占、最终句顺序、持久化顺序和取消后的继续处理。

## 代码与变更纪律

- 保持现有 C#/WPF 风格，优先最小、可审查的补丁。
- 保留 Apache-2.0 `LICENSE`、上游作者和来源声明；每次发布前按 `docs/RELEASE.md` 完成许可证与敏感数据核查。
- 不直接修改 `.github/workflows`、目标框架、运行时标识或依赖版本，除非任务明确包含构建系统变更。
- 不提交、推送、打标签或发布，除非用户明确授权；任何远端操作都需单独确认。

## 界面与字体

- 主界面采用 `src/styles/LectureTheme.xaml` 中的中性浅色资源；新增页面优先复用 `Lecture*` 资源，不重新引入蓝紫强调、渐变、发光或大面积装饰阴影。
- 普通界面文字使用 `LectureUiFontFamily`；`Wpf.Ui.Controls.SymbolIcon` 等图标控件保持自己的图标字体，不得跟随用户字体切换。
- 只枚举和使用用户本机已安装字体。不得从 Apple 或其他来源自动下载、复制或打包字体；默认使用 Windows 自带的 Segoe UI Variable/Segoe UI，并为简体中文保留 Microsoft YaHei UI 回退。
- 长字幕列表必须保留虚拟化；草稿更新不得重建整个列表。动效只用于短暂操作反馈和页面连续性，并遵守“减少动态效果”设置。
- 自动化或截图验收必须设置 `LECTURE_COPILOT_DATA_ROOT` 指向临时目录，禁止用真实 `%LOCALAPPDATA%\LectureCopilot.Dev` 数据做演示。

## 文档同步

- 构建命令、依赖、数据位置或发布阻塞项变化时，同步更新 `docs/PROJECT.md` 与 `docs/RELEASE.md`。
- 新增会持久化的数据时，必须同时记录存储位置、敏感性、备份和删除策略。
