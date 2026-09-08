PP-OCRv6 tiny 模型（RapidOCR 本地识别用）
=========================================

本目录四个文件是截图文字识别（RapidOCR 引擎）的本地模型，随程序分发：

  PP-OCRv6_det_tiny.onnx                    文本检测（tiny）
  PP-OCRv6_rec_tiny.onnx                    文本识别（tiny，中英日韩多语言）
  ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx  方向分类（PP-OCRv5 的 cls，v6 复用）
  ppocrv6_tiny_dict.txt                     识别词表

来源
----
模型来自 PaddleOCR / RapidOCR 官方仓库（PP-OCRv6 系列），由 RapidOcrNet
（https://github.com/BobLd/RapidOcrNet）项目整理提供。本机下载出处：
RapidOcrNet 仓库 RapidOcrNet/models/v6/ 与 models/v5/。

许可证
------
PaddleOCR 模型与 RapidOcrNet 均为 Apache-2.0。二次分发本目录文件时请保留
本说明与相应许可证文本（RapidOcrNet 的 LICENSE.txt/NOTICE.txt 可从其仓库获取）。

体积与性能
----------
全套约 7MB。实测 1449x678 的聊天截图：首次识别约 4s（含模型加载），
模型常驻后单次约 1.8s；识别质量明显好于 Windows 系统 OCR 的中文小字与图标混排。

想用更高精度的 small 模型：把 PP-OCRv6_det_small.onnx、PP-OCRv6_rec_small.onnx
和 ppocrv6_dict.txt 换成同名文件即可（体积约 31MB，单次识别约 5s）。