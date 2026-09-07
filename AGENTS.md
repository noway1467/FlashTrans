# FlashTrans 项目代理规范

本文件适用于本仓库及其所有子目录。项目规则补充全局规则；发生冲突时，在系统和开发者指令允许的范围内，以本文件的项目级明确约束为准。

## 1. 禁止使用子代理

- **本项目不要使用子代理。** 不得调用 `spawn_agent` 或其他代理委派工具，也不得启动并行代理进行探索、检索、评审、编码或测试。
- 所有上下文阅读、方案判断、代码修改、验证和交付均由当前主代理直接完成。
- 不得通过创建其他 Codex 任务、派生会话或外部代理进程绕过此限制。
- 即使全局规则建议或要求使用子代理，本项目仍以此禁令为准。任务复杂或上下文较长不是例外，应改用分步检索、分段阅读和简短进度记录。

## 2. 沟通与工作方式

- 默认使用简体中文交流、编写文档和解释问题；技术名词、API 和代码标识符保留标准英文命名。
- 先理解需求和现有实现，再修改文件。开始工作时阅读 `README.md`、相关代码，并检查 `git status --short --branch`、暂存及未暂存差异。
- 用户只要求计划、解释、评审或文档时，不擅自修改源代码。
- 改动前简要说明范围和验证方式；结论必须基于代码、日志或实际测试。不能确认的内容明确标注，不把猜测当事实。
- 优先修改现有文件，保持当前架构和风格；不做无关重构、全仓格式化或无关依赖升级。
- 注释重点解释原因、约束和边界，而不是重复代码含义。

## 3. 项目结构与技术边界

FlashTrans 是 Windows 原生划词翻译与截图工具，使用 C#、.NET 9、WPF 和 Win32/WinRT。目标框架以项目文件为准，当前为 `net9.0-windows10.0.19041.0`。

- `src/FlashTrans/FlashTrans.csproj`：主工程、目标框架、版本和资源声明。
- `src/FlashTrans/App.xaml.cs`、`AppHost*.cs`：应用启动、窗口和截图流程的组织入口。
- `src/FlashTrans/Core/`：翻译调度、缓存、网络和配置模型。
- `src/FlashTrans/Providers/`：各翻译源实现。
- `src/FlashTrans/Services/`：OCR、设置、主题等服务。
- `src/FlashTrans/Interop/`：Windows 原生互操作和屏幕像素处理。
- `src/FlashTrans/Views/`、`Themes/`：WPF 窗口、交互与主题资源。
- `tests/FlashTrans.SelfTest/`：真实 WPF 环境中的自测程序，不是普通 `dotnet test` 测试项目。
- `tools/`：构建发布、压缩打包及资源检查脚本。
- `dist/`、`shots/`、`bin/`、`obj/`：发布、截图或构建产物，不作为手工修改的源码。

仓库遗留的 `node_modules/` 和 `package-lock.json` 不代表当前工程使用 Node.js 或 Electron；不要因此运行前端依赖安装或重建另一套应用架构。

OCR 使用本机 `Windows.Media.Ocr`，可用语言取决于已安装的 Windows OCR 语言包。不得在没有用户明确要求时改成联网识别或上传截图。纠偏与归一化需要防止误改合法字符、代码、数字和符号，不能通过硬编码样例文本伪造识别质量。

## 4. Windows 与文件安全

- 默认使用 PowerShell，区分 PowerShell、cmd 和 Bash 语法。
- 文件检索优先使用 `rg --files`，内容检索优先使用 `rg -n`；读文件优先 `Get-Content -LiteralPath`，手工修改优先补丁式编辑。
- 保留用户已有的暂存和未暂存改动，不执行未经要求的重置、清理、覆盖、移动或删除。
- 递归删除或移动前，必须核验目标绝对路径确实位于预期临时目录或发布目录内；不得跨 shell 拼接删除命令。
- 不自动结束用户正在运行的 FlashTrans。后台辅助程序使用隐藏窗口；清理时只终止本次工作创建的进程。
- 临时测试文件放系统临时目录或明确的项目临时目录，测试完成后安全清理。
- 不读取、输出或提交用户密钥、令牌、个人设置及敏感日志。默认配置位于 `%AppData%\FlashTrans`；便携模式使用 exe 同目录的 `portable.txt` 和 `data/`。
- 测试使用隔离配置。自测工程会复制 `portable.txt` 到输出目录，不得移除它而让测试写入用户的真实配置。

## 5. 构建与验证

以下命令从项目根目录执行，需要 Windows 和 .NET 9 SDK。

### 构建主程序

```powershell
dotnet build .\src\FlashTrans\FlashTrans.csproj -c Release --nologo
```

### 构建并运行完整自测

```powershell
dotnet build .\tests\FlashTrans.SelfTest\FlashTrans.SelfTest.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw '自测构建失败，停止执行，避免误跑旧产物。' }
dotnet .\tests\FlashTrans.SelfTest\bin\Release\net9.0-windows10.0.19041.0\FlashTrans.SelfTest.dll
```

### 定向自测

```powershell
$selfTest = '.\tests\FlashTrans.SelfTest\bin\Release\net9.0-windows10.0.19041.0\FlashTrans.SelfTest.dll'
dotnet $selfTest --only-clipboard
dotnet $selfTest --only-longshot
dotnet $selfTest --only-ocr
```

- 定向运行前先构建当前代码，不能用旧 DLL 证明新修改通过。
- `--only-clipboard` 和 `--only-longshot` 会在各自分支提前返回，**不覆盖完整 OCR、窗口和录制测试**。OCR 可使用 `--only-ocr` 定向验证；交付前仍需按改动范围运行完整自测。
- 完整自测涉及真实窗口、剪贴板、截图和录制，应确认桌面环境可用，并避免干扰用户正在进行的操作。
- 只有明确需要时才使用 `--net`，避免常规验证意外请求外部翻译服务。`--ocr-corpus` 是显式公开样例评测，缺少已验证缓存时会下载图片；输出字符错误率，不把执行成功当作全部识别正确。其他参数以 `Program.cs` 为准。
- 功能修改不能只靠编译通过交付。根据改动补充定向回归，必要时执行启动烟测、真实截图识别、交互或发布包验证。
- OCR 多语言测试区分“文本处理规则通过”和“对应 Windows 语言包实际识别通过”；没有安装语言包时明确说明缺口。
- 测试失败必须记录失败项和原因，不得未经复现就归类为既有问题或环境问题，也不得跳过错误后宣称全部通过。

## 6. 发布与 Git

- 仅在用户明确要求时执行打包、版本升级、提交或推送。
- 发布入口为 `tools\publish.cmd fast`、`tools\publish.cmd small` 或 `tools\publish.cmd both`。`fast` 是自包含版本，`small` 需要已安装 .NET 9 Desktop Runtime。
- 压缩入口为 `tools\pack-release.ps1`，通过 `-Version` 指定版本；它只压缩已有发布目录，不会重新编译。发布目录在 `dist/`，压缩包在 `dist/release/`。
- 发布脚本会清理对应输出目录，打包脚本会覆盖同名压缩包。运行前确认目标路径、已有产物和便携数据，不能误删用户资产。
- 发布遇到运行实例锁文件时，不强杀程序、不覆盖在用目录；说明阻塞并采用经确认的独立输出目录或等用户退出。
- 版本升级需同步项目版本、打包脚本默认版本和相关自测断言，确认压缩包包含本次构建结果。
- 提交前检查暂存差异、未暂存差异和空白错误；只纳入本任务文件，不随手执行 `git add .`。
- Git commit 使用中文描述，建议 `type(scope): 中文说明`。未经授权不强推、不改写历史、不更改远端配置。

## 7. 交付要求

- 汇报改了什么、为什么改、验证了什么、还有哪些风险，以及是否需要用户操作。
- 没有运行的测试直接注明；纯文档修改说明未运行功能测试即可。
- 引用本地文件使用绝对路径链接。涉及发布时给出实际产物位置；涉及提交或推送时给出真实结果，不把准备完成写成已经完成。
