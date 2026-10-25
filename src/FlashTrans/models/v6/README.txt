PP-OCRv6 模型（RapidOCR 本地识别用）
=========================================

当前运行时使用以下文件：

  PP-OCRv6_det_tiny.onnx                    文本检测（tiny）
  PP-OCRv6_rec_small.onnx                   文本识别（small，中英日及拉丁语系）
  ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx  方向分类（PP-OCRv5 的 cls，v6 复用）
  ppocrv6_small_dict.txt                    small 原始识别词表，18708 项

旧 tiny 识别模型及词表留在源码中供对照，不再加载。tiny 的 6904 项词表没有
日语假名，不能识别「の」；small 也不等于韩语通用模型，韩语需系统 OCR 语言包。
不能只更换词表或把 small 重命名成 tiny：词表索引必须与识别网络的输出一致。

来源
----
模型来自 PaddleOCR / RapidOCR 官方仓库（PP-OCRv6 系列），由 RapidOcrNet
（https://github.com/BobLd/RapidOcrNet）项目整理提供。本机下载出处：
检测、方向及旧 tiny 来自 RapidOcrNet/models/v6/ 与 models/v5/。

small 模型根据 RapidOCR 官方路由清单下载（2026-10-25）：
https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml
该清单明确区分 ppocrv6_tiny_langs（不含 japan）与 ppocrv6_langs（含 japan）。

模型（固定 v3.10.0）：
https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx
SHA256（已与官方清单核对）：
6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884

词表（同一版本，原始字节保存为 ppocrv6_small_dict.txt）：
https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt
SHA256（本次下载的复验值）：
b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d

许可证
------
PaddleOCR 模型与 RapidOcrNet 均为 Apache-2.0。二次分发本目录文件时请保留
本说明与相应许可证文本（RapidOcrNet 的 LICENSE.txt/NOTICE.txt 可从其仓库获取）。

体积与性能
----------
small 识别模型为 21234383 字节，检测器仍使用 tiny，避免无依据地增加检测开销。
耗时随截图内容、CPU 和首次加载变化；不可沿用旧 tiny 的性能数字。
普通 OCR 能识别部分公式字符，但不支持复杂二维公式、手写公式或 LaTeX 结构恢复。
