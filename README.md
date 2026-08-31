# FlashTrans

Windows 划词翻译 + 截图工具软件

聚合 17 种翻译源（免费接口 + 官方 API + AI 大模型）

## 功能

- **划词翻译**：选中文本后出现小图标，点一下就译；也可设为选中即译
- **聚合多源**：免费源开箱可用，无需配置；官方 API 与 AI 源填密钥即可。某个源报错就自动换
- **截图 OCR**：框选屏幕任意区域，标注后保存、复制，或识别文字（本机识别，不联网）。支持长截图
- **多语言同译**：一次翻成多个目标语言，结果并排
- **双语对照**：原文与译文逐段对照
- **词典**：单词自动显示音标与释义，一键跳转欧路词典
- 深色/浅色主题、强调色、字号、透明度、紧凑模式；窗口可自由缩放并记住位置

## 快速开始

下载后直接运行 `FlashTrans.exe`。免费源已默认启用，选中任意文本按 `Ctrl+Alt+Q` 即可。

自己编译（需要 .NET 9 SDK）：

```
tools\publish.cmd fast     # 自包含单文件，什么都不用装（推荐）
tools\publish.cmd small    # 依赖已安装的 .NET 9 Desktop Runtime，体积最小
```

产物在 `dist\`。打包前先退出正在运行的实例（托盘右键最后一项）。

`fast` 包免安装，但**整个文件夹要一起拷**——单文件发布不会把 WPF 的原生 DLL 塞进 exe，只拿 exe 走会启动失败。两个包都要保留同目录下的 `Assets\app.ico`，托盘图标从它加载。

## 默认快捷键

| 快捷键 | 作用 |
| --- | --- |
| `Ctrl+Alt+Q` | 翻译当前选中的文本 |
| `Ctrl+Alt+W` | 显示/隐藏主窗口 |
| `Ctrl+Alt+E` | 翻译剪贴板内容 |
| `Ctrl+Alt+S` | 开关划词监听 |
| `Ctrl+Alt+A` | 截图 |

弹窗内：`Esc` 关闭 · `Ctrl+Tab` 换源 · `Ctrl+C` 复制译文 · `Ctrl+D` 查欧路词典 · `Ctrl+E` 展开到主窗口 · `Ctrl+1..9` 选标签。

全部可在「设置 → 快捷键」里改。另可开启「连按两次 Ctrl 唤醒」。

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

按 `Ctrl+Alt+A`（或托盘右键 → 截图），屏幕定格拖动框选。提供工具条。`Esc` 或右键取消。

OCR 用的是 Windows 自带的 `Windows.Media.Ocr`，**不联网、不上传、不要密钥**。

支持截图并翻译，支持快捷键。

## 设置与数据

配置文件：`%AppData%\FlashTrans\settings.json`
日志：`%AppData%\FlashTrans\flashtrans.log`

**便携模式**：在 exe 同目录放一个 `portable.txt`，配置与日志改存到 `.\data`，不写注册表、不碰 `%AppData%`。

**API 密钥**用 Windows DPAPI 加密后再落盘，只有当前 Windows 用户能解出来。换机器或换用户需要重新填写。

翻译结果有内存缓存，重复查询直接命中。保留时长默认 12 小时，可在「设置 → 通用 → 缓存与网络」里改。

**开机自启**：「设置 → 通用 → 开机启动」写入 `HKCU\...\Run`，带 `--tray` 静默启动到托盘。

## 自测

```
dotnet build tests\FlashTrans.SelfTest -c Release
tests\FlashTrans.SelfTest\bin\Release\net9.0-windows10.0.19041.0\FlashTrans.SelfTest.exe
```

在真实 WPF 环境里把每个窗口、每个设置页都构造一遍，另外覆盖缓存淘汰、抓屏与 OCR、标注的命中与改形状、长截图的位移计算。不带参数 71 项，`--net` 加上联网的共 79 项。

其他参数：`--net` 测各源连通性，`--timing` 打出聚合时每个源的耗时，`--shot` 把各窗口和截图工具条渲成 PNG 写到 `shots\`（样式改动只能看图，断言看不出好不好看），`--benchmark` 测启动耗时。

## 技术栈

WPF / .NET 9 / C#，约 1 万行。Win32 交互（全局热键、低级鼠标键盘钩子、剪贴板轮询、托盘图标、多显示器 DPI、GDI 抓屏）走 `Interop` 目录下的 P/Invoke。

**依赖**：没有任何第三方库。唯一的非框架引用是微软自己的 Windows SDK 投影，靠目标框架 `net9.0-windows10.0.19041.0` 隐式带进来，只为调用系统的 `Windows.Media.Ocr`。

## 许可

尚未指定。免费源走的是各家网页端接口，仅供个人使用；商用请换成对应的官方 API。
