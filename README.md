# FlashTrans

[![Release](https://img.shields.io/github/v/release/noway1467/FlashTrans?label=release&color=0088cc)](https://github.com/noway1467/FlashTrans/releases/latest)

Windows 划词翻译、文件翻译与截图工具。

## 下载安装

前往 [最新 Release](https://github.com/noway1467/FlashTrans/releases/latest) 下载，解压后运行 `FlashTrans.exe`。默认启用免费翻译源，无需填写 API Key。

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
| 截图标注 | 区域截图、标注、复制、保存和屏幕贴图；贴图支持缩放、透明度和鼠标穿透。 |
| 长截图与录制 | 滚动长截图，录制 WebP / GIF / MP4。 |
| 本地 OCR | RapidOCR / Windows OCR，支持提取文字和识别后翻译。 |
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
powershell -NoProfile -File .\tools\pack-release.ps1 -Version 1.9.3
```

发布目录位于 `dist/`，压缩包位于 `dist/release/`。发布会原位更新已有目录，并保留便携数据和用户文件；压缩仅收录本次构建的程序文件。

## 许可

项目尚未指定许可证。免费源仅适合个人使用，商用请使用官方授权接口。第三方组件许可证随程序放在 `Assets/licenses/` 等对应组件目录。
