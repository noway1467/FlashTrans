# FlashTrans

Windows 划词翻译、文件翻译与截图工具。

## 下载安装 [![Release](https://img.shields.io/badge/release-v1.9.5-0088cc)](https://github.com/noway1467/FlashTrans/releases/latest)

点击上方版本徽章下载，解压后运行 `FlashTrans.exe`。默认启用免费翻译源，无需填写 API Key。

| 版本 | 运行要求 |
| --- | --- |
| `fast`（推荐） | 自带运行时，解压即用 |
| `small` | 需安装 .NET 9 Desktop Runtime |

## 功能

| 功能 | 说明 |
| --- | --- |
| 划词翻译 | 快捷键取词、自动划词、剪贴板翻译，支持剪贴板还原和应用排除。 |
| 多源翻译 | 免费源、官方 API、AI 接口及本地模型。 |
| 译文展示 | 多源对比、双语对照、多目标语言和单词词典。 |
| 文件翻译 | EPUB、TXT、Markdown、DOCX、文本层 PDF；支持断点续译、术语表、双语 HTML 和任务历史。 |
| 截图标注 | 区域截图、标注、原图取色、复制、保存和屏幕贴图；文字编辑框可直接拖动边缘。贴图支持缩放、透明度和鼠标穿透。 |
| 长截图与录制 | 原像素分片预览、适应宽度与缩放；录制 WebP / GIF / MP4。 |
| 本地 OCR | RapidOCR small 支持中英日及拉丁语系；其他脚本自动尝试已安装的 Windows OCR 包，支持提取文字和识别后翻译。 |
| 敏感内容打码 | 本地检测敏感文字、自定义关键词、手动实心遮挡。 |
| 快速启动 | 自选程序、快捷方式和文件夹，支持搜索、改名、排序及快捷键呼出。 |
| 外观设置 | 深浅主题、字体、字号、透明度、紧凑模式和窗口位置记忆。 |

> 文件翻译不覆盖原件；PDF 仅支持文本层并重排输出，不支持扫描件或原版式还原。复杂格式和术语保护推荐使用 AI 源。
>
> 自动打码仅适用于普通静态截图，不保护长截图或录制，发送前请人工核对。

## 翻译源

| 类型 | 支持的服务 |
| --- | --- |
| 默认免费源 | 谷歌、微软、有道、腾讯交互翻译 |
| 免费 / 自建 | LibreTranslate、MyMemory、Lingva、DeepLX |
| 官方 API | DeepL、Azure、Google Cloud、百度、腾讯、彩云小译 |
| AI | OpenAI 兼容接口、Gemini、Claude；支持 Ollama / LM Studio 本地服务 |

在「设置 → 翻译源」中添加和管理。AI 源支持拉取模型列表；费用与额度以服务商为准。

## 数据与隐私

- **本地配置**：默认位于 `%AppData%\FlashTrans`；便携模式在 exe 旁放置空的 `portable.txt`，配置写入同目录的 `data\`。
- **加密保存**：API Key 和可选的文件续译进度使用当前 Windows 账户的 DPAPI 加密。
- **联网范围**：OCR 和自动打码在本机完成，不上传截图；翻译文字会发送到所选服务。Windows OCR 需安装对应语言包。

### 截图取色与文字移动

- 框选后点击工具条的取色器（滴管），移动鼠标查看原截图像素的 HEX / RGB；单击复制 `#RRGGBB`，`Shift+单击`复制 `rgb(R, G, B)`，右键或 `Esc` 返回标注。
- 取色不关闭截图、不改选区，也不会采入蒙层或标注的颜色。
- 文字输入框的外围可直接按住拖动，内部仍用于输入和选字；提交后保持文字工具选中也能拖动已有文字，无需先关闭工具。

### OCR 能力边界

- 默认使用 tiny 检测器 + small 识别模型。旧 tiny 识别词表没有日语假名，不能通过后处理可靠修好；新模型与词表来源、哈希见 `src/FlashTrans/models/v6/README.txt`。
- OCR 选项位于「设置 → OCR」，不再放在截图页底部；原有引擎、语言及复制选项沿用，无需重新配置。
- 日语识别不依赖 Windows 日语语言包。「自动」和「RapidOCR」都会试识别已安装的韩语、俄语、阿拉伯语、泰语等系统包，根据结果中的脚本证据回退；即使 RapidOCR 误读出了非空文字，也会尝试。没有安装的语言包不会被调用，不联网识别或自动下载。
- 「识别语言」默认自动检测；手动指定这些非 RapidOCR 语言时直接使用对应系统包，缺包会明确提示，不会冒用中文或英文包。安装 Windows「光学字符识别」可选功能后需重启闪译。短词、混合语言可能判断不准，可手动指定语言；超过 Windows OCR 尺寸上限的图片不做自动回退。
- 普通公式字符、上下标和变量尽量原样保留，不猜改变量或补运算符。复杂二维分式、矩阵、手写公式及转 LaTeX 尚不支持，数学结果必须核对。


## 默认快捷键

| 快捷键 | 作用 |
| --- | --- |
| `Ctrl+Alt+Q` | 翻译选中的文本 |
| `Ctrl+Alt+W` | 显示 / 隐藏主窗口 |
| `Ctrl+Alt+E` | 翻译剪贴板内容 |
| `Ctrl+Alt+S` | 开关划词翻译 |
| `F1` | 截图 |
| `Ctrl+Alt+H` | 收起 / 叫回翻译弹窗 |
| `Ctrl+Alt+Space` | 呼出 / 隐藏快速启动面板 |

可在「设置 → 快捷键」中修改；冲突会提示并保留原快捷键。在「设置 → 快速启动」中添加项目，也可从托盘菜单打开面板。

## 从源码构建

需要 Windows 和 .NET 9 SDK，在仓库根目录执行：

```powershell
dotnet build .\src\FlashTrans\FlashTrans.csproj -c Release --nologo
```

生成发布包：

```powershell
.\tools\publish.cmd fast   # 自包含版本
.\tools\publish.cmd small  # 依赖 .NET 9 Desktop Runtime
powershell -NoProfile -File .\tools\pack-release.ps1 -Version 1.9.5
```

发布目录位于 `dist/`，压缩包位于 `dist/release/`。发布会原位更新已有目录，并保留便携数据和用户文件；压缩仅收录本次构建的程序文件。

OCR / 截图回归：先构建 `tests/FlashTrans.SelfTest/FlashTrans.SelfTest.csproj`，再运行其输出 DLL 的 `--only-ocr` 或 `--only-capture`。前者包含日语、圈号与常见公式的真实本地模型识别（比较时忽略空白并折叠全角 ASCII，不代表排版无损）；后者包含真实鼠标拖动和剪贴板取色，会短暂占用桌面。测试使用隔离配置，不加 `--net` 不请求翻译服务。

长截图预览支持「原始大小」、`Ctrl+0` 恢复 100%、`Ctrl+滚轮` 缩放；复制和保存始终使用无损原图。动图保存优化及本机对照测试见 [截图与录制优化记录](tests/FlashTrans.SelfTest/CAPTURE-PERFORMANCE.md)。

## 许可

项目尚未指定许可证。免费源仅适合个人使用，商用请使用官方授权接口。第三方组件许可证随程序放在 `Assets/licenses/` 等对应组件目录。
