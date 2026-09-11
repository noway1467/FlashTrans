# FlashTrans

Windows 下的划词翻译和截图工具。

## 能干什么

- 划词翻译：选中文字后按 `Ctrl+Alt+Q`，或让程序在选中后直接弹译文。
- 多源翻译：默认开谷歌、微软、有道、腾讯交互翻译；也可以自己加官方 API 和 AI 源。
- 截图：按 `F1` 框选区域，能标注、复制、保存、钉住、识别文字、识别后翻译。
- 长截图和录制：支持长截图，也能录成 WebP / GIF / MP4。
- 结果显示：可开多列对比、双语对照、多个目标语言；单词会显示词典结果。
- 外观：深浅色主题、字号、字体、透明度、紧凑模式，窗口位置会记住。

## 快速开始

解压后运行 `FlashTrans.exe`。默认只开免费源，不用填 Key。

自编译需要 .NET 9 SDK：

```powershell
tools\publish.cmd fast   # 自包含包
tools\publish.cmd small  # 依赖 .NET 9 Desktop Runtime
```

产物在 `dist\`。打包前先退出正在运行的实例。

## 默认快捷键

| 快捷键 | 作用 |
| --- | --- |
| `Ctrl+Alt+Q` | 翻译选中的文本 |
| `Ctrl+Alt+W` | 显示 / 隐藏主窗口 |
| `Ctrl+Alt+E` | 翻译剪贴板内容 |
| `Ctrl+Alt+S` | 开关划词翻译 |
| `F1` | 截图 |
| `Ctrl+Alt+H` | 收起 / 叫回翻译弹窗 |

其它热键在「设置 → 快捷键」里改。

## 翻译源

默认启用 4 个免费源：谷歌、微软、有道、腾讯交互翻译。

可选源分三类：

- 免费 / 自建：LibreTranslate、MyMemory、Lingva、DeepLX。
- 官方 API：DeepL、Azure、Google Cloud、百度、腾讯、彩云小译。
- AI：OpenAI 兼容接口、Gemini、Claude。

费用和额度按各家官方文档为准。设置里可以测试、复制、排序和切换默认源。

## 截图和 OCR

- `F1` 框选，`Esc` 取消，回车执行默认动作。
- 工具条里有标注、长截图、录制、识别、钉住、保存、复制。
- 钉住后可以把截图贴在屏幕上；鼠标移到贴图上能复制、保存或销毁。
- OCR 用本机 RapidOCR 或 Windows OCR，不上传截图。小字、花底色、竖排可能识别不准，重要内容要人工核对。

## 数据

- 正常模式：配置在 `%AppData%\FlashTrans`。
- 便携模式：exe 旁边放一个空的 `portable.txt`，配置写到 `data\`。
- API Key 用 Windows DPAPI 加密后保存。

## 许可

尚未指定。免费源走各家公开接口，仅适合个人使用；商用请换成官方授权接口。
