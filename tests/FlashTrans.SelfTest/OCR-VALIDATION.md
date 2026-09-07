# FlashTrans 1.7.9 OCR 评测记录

日期：2026-09-07。以下是同一台 Windows 机器上的真实 Windows.Media.Ocr 结果，不是模型总体准确率榜单。

## 环境与比较口径

- .NET 9 SDK 9.0.308；唯一安装的 OCR 包为 `zh-Hans-CN`，英文也由该包识别。没有安装额外 OCR 引擎或语言包。
- 基线是本轮文本安全修复完成后、增加平滑留白和方向回退之前的工作区，不是远端历史版本。
- CER = Unicode 字符编辑距离 / 真值字符数。双方先做 NFC 和全角 ASCII 折叠、去除空白；仍区分大小写并保留其他符号。忽略空白不代表排版或英文分词一定正确。
- 六题规模很小，其中两题是同一段英文的不同方向；不能外推为全部截图的识别率。耗时仅为单次运行记录，不是统计性能基准。

## 公开图片实测

| 样例 | 基线错误数 / 字符数 | 优化后错误数 / 字符数 | 优化后 CER |
| --- | ---: | ---: | ---: |
| Tesseract 英文段落 phototest | 6 / 225 | 6 / 225 | 2.67% |
| 同段落横转 90° phototestrot | 220 / 225 | 6 / 225 | 2.67% |
| 紧裁切数字 12 | 2 / 2 | 2 / 2 | 100.00% |
| PaddleOCR 中文 ch_doc1 | 0 / 10 | 0 / 10 | 0.00% |
| PaddleOCR 中文 ch_doc3 | 3 / 10 | 0 / 10 | 0.00% |
| PaddleOCR 英文 PAIN | 0 / 4 | 0 / 4 | 0.00% |

中文 ch_doc3 的真值是「4年工作报告》中指出」。基线漏掉三个字符；新增平滑放大、背景留白后，原生 OCR 恢复了它们，没有针对该句替换文本。横转段落通过受限方向回退恢复；原先已正确的两条短行没有回退。

数字 12 仍返回空串。英文段落仍存在 I/l、O/o 等误识别。不能用已知答案回填这些错误，也不能把“程序没报错”写成“样例全部通过”。

## 来源与复现

- [Tesseract 官方测试仓库](https://github.com/tesseract-ocr/test)；[图片说明](https://github.com/tesseract-ocr/test/blob/main/testing/README.md)。
- [phototest 原图](https://github.com/tesseract-ocr/test/blob/main/testing/phototest.tif)、[横转图](https://github.com/tesseract-ocr/test/blob/main/testing/phototestrot.tif)、[官方真值](https://github.com/tesseract-ocr/test/blob/main/testing/phototest.txt)、[数字 12](https://github.com/tesseract-ocr/test/blob/main/testing/12.tif)。
- [PaddleOCR ch_doc1](https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/datasets/images/ch_doc1.jpg)、[ch_doc3](https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/datasets/images/ch_doc3.jpg)、[英文 PAIN](https://github.com/PaddlePaddle/PaddleOCR/blob/main/deploy/avh/imgs_words_en/word_10.png)。
- 英文段落采用官方真值；其他短行逐张查看原图后人工转录。图片的固定 Git blob 哈希和参考文本在 `OcrCorpusProbe.cs`，不依赖浮动分支上的图片内容。
- 两个仓库均声明 Apache-2.0；本仓库不重新分发其图片，只保留引用和校验值。

从项目根目录先构建自测，再执行：

```powershell
dotnet .\tests\FlashTrans.SelfTest\bin\Release\net9.0-windows10.0.19041.0\FlashTrans.SelfTest.dll --ocr-corpus
```

该入口优先使用 `shots/ocr-corpus-cache/` 的已校验原图，缺失时访问 GitHub 公共 API；无缓存且被限流或断网时会明确失败，不会跳过样例。JSON 结果写入 `shots/ocr-corpus-results.json`，包含来源、图片哈希、真值、识别文本、错误数和耗时。普通完整自测不触发这些下载。

## 配套功能回归

- 空白截图与缺少语言包分别处理，已取消请求能够退出。
- 不再推测代码块缺失的右括号或将合法的 household、threshold 等标识符改成 Id。
- 保留英文标点后的空格、首行缩进、PiB/Mb 等不同单位及 cm²、½、①、emoji 等 Unicode 语义。
- 多通道合并按行对应，不串改重复字段，也不把 125 合并成 1.25。
- RTL 使用系统返回的逻辑词序，防止混排英文片段被简单倒序；这里只验证排版规则，没有声称完成阿拉伯语或希伯来语语言包实测。
- 截图隐身探针验证实际前景、背景和恢复状态，兼容新系统透出背景及旧环境黑色保护；背景置顶避免其他用户窗口污染对照。

最终功能自测、发布包烟测与 Git 交付结果以本次交付说明为准；本记录只描述 OCR 对照实验和回归范围。
