# FlashTrans

Windows 划词翻译 + 截图工具软件

聚合 17 种翻译源（免费接口 + 官方 API + AI 大模型）

## 功能

- **划词翻译**：选中文本后出现小图标，点一下就译；也可设为选中即译
- **聚合多源**：免费源开箱可用，无需配置；官方 API 与 AI 源填密钥即可。失败自动换源
- **截图 OCR**：支持长截图，框选屏幕任意区域，标注后保存、复制，或识别文字（本机识别，不联网）
- **录制动图**：支持 WebP / GIF / MP4，帧率最高 60 fps；录制期间并行写入临时帧，尽量减少大选区卡顿（部分 GIF 播放器会限制高帧率，优先用 WebP / MP4）
- **多语言同译**：一次翻成多个目标语言，结果并排且每种语言都可独立复制
- **双语对照**：原文与译文逐段对照，并可分别复制原文和译文；安装对应系统语音后可播放译文
- **词典**：单词自动显示音标与释义，一键跳转欧路词典
- 深色/浅色主题、强调色、字号、透明度、紧凑模式；窗口可自由缩放并记住位置

## 快速开始

下载后直接运行 `FlashTrans.exe`。免费源已默认启用，选中任意文本按 `Ctrl+Alt+Q` 即可。

自编译（需要 .NET 9 SDK）：

```
tools\publish.cmd fast     # 自包含单文件，什么都不用装（推荐）
tools\publish.cmd small    # 依赖已安装的 .NET 9 Desktop Runtime，体积最小
```
产物在 `dist\`。打包前先退出正在运行的实例（托盘右键最后一项）。

发布包启动烟测可在独立目录放置 `portable.txt` 后运行 `FlashTrans.exe --tray --benchmark`。诊断实例会记录启动日志并退出，不唤醒已有实例、不注册全局热键、不监听选区、不同步开机自启；因此该模式的耗时不等同于正常启动的完整系统集成耗时。

## 默认快捷键

| 快捷键 | 作用 |
| --- | --- |
| `Ctrl+Alt+Q` | 翻译当前选中的文本 |
| `Ctrl+Alt+W` | 显示/隐藏主窗口 |
| `Ctrl+Alt+E` | 翻译剪贴板内容 |
| `Ctrl+Alt+S` | 开关划词监听 |
| `Ctrl+Alt+A` | 截图 |
| `Ctrl+Alt+H` | 收起 / 叫回翻译弹窗（被盖住时先抬到最前） |

## 翻译源

**免费，无需密钥**（默认启用）

谷歌翻译、微软翻译、有道翻译、腾讯交互翻译（国内直连）、LibreTranslate / MyMemory / Lingva（公共实例，也可填自建地址）、DeepLX（自建服务）。

**官方 API，有免费额度**

| 源 | 免费额度 |
| --- | --- |
| DeepL API Free | 50 万字符/月 |
| Azure 翻译器 | 200 万字符/月 |
| Google Cloud Translation | 50 万字符/月 |
| 腾讯翻译君 | 500 万字符/月 |
| 百度翻译 | 通用版每月有免费额度 |
| 彩云小译 | 有免费额度（仅中英日） |

**AI 翻译**

OpenAI 兼容接口，内置 10 个预设一键填好地址与模型：OpenAI、DeepSeek、Kimi、智谱 GLM、通义千问、Groq、硅基流动、OpenRouter、Ollama 与 LM Studio（本地离线，无需 Key）。另有 Gemini 和 Claude。

AI 源支持流式输出，译文边生成边显示，也可单独设定风格要求（比如「口语化」「保留术语」）。

设置里每个源都能测试连通性、复制一份、设为默认、上下调整顺序。

## 截图

OCR 内置两套引擎：**RapidOCR**（PP-OCRv6 本地模型，不联网、不上传、不要密钥）和 Windows 自带的 `Windows.Media.Ocr`。

在「设置 → 截图 → OCR」中可切换引擎：`自动`（默认，优先使用 RapidOCR）`系统`（Windows.Media.Ocr）与 `RapidOCR`（自带 v6 模型）。系统 OCR 的识别语言跟随下拉选择，默认自动比较已安装的 OCR 语言包，不跟随翻译源语言；RapidOCR 内置中英文、数字与常见符号识别，无需装语言包。没有可用引擎时会给出提示。

- 识别会比较最近邻放大、灰度/二值化和平滑留白结果，保留列表换行、缩进和已识别到的符号。
- 中小竖图识别明显不足时，会有限度尝试 90° 方向回退；不是任意旋转、复杂竖排或图标识别器。
- 默认按截图内的 `Ctrl+D` 会打开可编辑的结果窗口。勾选「按『识别文字』快捷键后直接复制并关闭」后，该快捷键及长截图中的 `Ctrl+D` 改为直接复制；鼠标按钮仍打开编辑窗口，`Ctrl+Shift+D` 仍执行识别并翻译。此开关默认关闭。
- 数字、短码和很小的文字仍可能漏识别。程序不会凭空补齐缺失括号，也不会把普通代码标识符强行猜成 `Id`。重要内容请在结果窗口核对。

支持长截图，录动图/视频，截图并翻译。长截图会等待滚动和动态内容稳定后再拼接，避免论坛懒加载时把半屏内容接进去。

### OCR 自测

从项目根目录运行（需要 Windows 和 .NET 9 SDK）：

```powershell
dotnet build .\tests\FlashTrans.SelfTest\FlashTrans.SelfTest.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw '构建失败，停止运行旧测试产物。' }
$selfTest = '.\tests\FlashTrans.SelfTest\bin\Release\net9.0-windows10.0.19041.0\FlashTrans.SelfTest.dll'
dotnet $selfTest --only-ocr    # 定向离线 OCR 回归
dotnet $selfTest               # 完整 WPF 功能自测
dotnet $selfTest --ocr-corpus  # 显式公开样例评测；缺少缓存时联网下载
```

公开评测使用六张固定哈希的 Tesseract / PaddleOCR 样例，不上传截图、不安装第三方 OCR 引擎。图片缓存及 JSON 结果在 `shots/ocr-corpus-cache/`、`shots/ocr-corpus-results.json`，不会进入 Git。评测执行成功不等于识别全对；详见 [OCR 评测记录](tests/FlashTrans.SelfTest/OCR-VALIDATION.md)。


## 设置与数据

配置文件：`%AppData%\FlashTrans\settings.json`
日志：`%AppData%\FlashTrans\flashtrans.log`

**便携模式**：在 exe 同目录放一个 `portable.txt`，配置与日志改存到 `.\data`，不碰 `%AppData%`。

**API 密钥**用 Windows DPAPI 加密后再落盘，只有当前 Windows 用户能解出来。换机器或换用户需要重新填写。

翻译结果有内存缓存，重复查询直接命中。保留时长默认 12 小时，可在「设置 → 通用 → 缓存与网络」里改。

**开机自启**：「设置 → 通用 → 开机启动」写入 `HKCU\...\Run`，带 `--tray` 静默启动到托盘（只有勾了才写注册表，便携模式也一样）。这一项存的是 exe 的绝对路径，换目录或换成新版本的文件夹后那条路径就失效了，所以每次启动会自动把它改写成当前 exe —— 不用重新勾一遍。


## 许可

尚未指定。免费源走的是各家网页端接口，仅供个人使用；商用请换成对应的官方 API。
