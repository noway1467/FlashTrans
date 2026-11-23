FlashTrans 阶段 1 瘦身验证记录，2026-11-22，版本保持 1.9.10。

本轮直接替换 RapidOcrNet / SkiaSharp 和 CsWinRT 投影，保留 C#、.NET 9、WPF 与全部发布 OCR 模型。只在系统临时目录发布和测试，未覆盖仓库 `dist/`，未修改用户真实配置，未提交或推送 Git。

现有发布目录名为 `FlashTrans-1.9.0-win-x64-fast/small`，exe 实际版本为 `1.9.10.0`。基线按旧发布清单逐文件校验哈希后复制到隔离目录，未复制便携数据、用户设置或日志。测量前后原 `dist/` 清单内所有文件的 SHA256 均一致。

以下体积为程序文件总和，单位为 MiB（1,048,576 字节）；不包含便携标记、配置、日志、测试宿主与发布清单。ZIP 使用与现有打包脚本相同的 `CompressionLevel.Optimal`。

| 方案 | 文件总量 | exe | ZIP | 相对基线文件减幅 | 相对基线 ZIP 减幅 |
|---|---:|---:|---:|---:|---:|
| 原 fast | 213.90 | 155.77 | 98.68 | — | — |
| 新 fast，R2R 开启并排除冷路径 | 105.01 | 57.42 | 82.32 | 50.91% | 16.57% |
| 新 fast，R2R 关闭 | 104.04 | 56.45 | 81.36 | 51.36% | 17.55% |
| 原 small | 84.54 | 0.18 | 41.11 | — | — |
| 新 small | 49.26 | 0.18 | 30.03 | 41.73% | 26.95% |

fast 继续使用 `PublishReadyToRun=true`。关闭 R2R 仅节省约 0.97 MiB，交错测量中的就绪中位数增加 80 ms，工作集增加约 6 MiB，收益不足以替换当前方案。两个 fast 方案均启用 `EnableCompressionInSingleFile=true`、`DebugType=none`、`DebugSymbols=false`，未启用裁剪或 NativeAOT。

`--benchmark` 从各自隔离的便携目录启动。所有编译、ZIP 和发布文件回归完成后，将五种方案交错运行 9 次，轮换顺序；舍去各方案首轮，对余下 8 次取中位数。45 次启动均退出码为 0、产生完整诊断记录，未发现未处理异常；新方案未提前加载 OCR / PDF 库。完整逐次数据和文件清单保存在同目录 `SLIMMING-STAGE1.json`。

| 方案 | 就绪中位数 ms | 空闲中位数 ms | 工作集 MiB | 余下 8 次就绪范围 ms |
|---|---:|---:|---:|---:|
| 原 fast | 949 | 1003.5 | 78 | 894–1301 |
| 新 fast，R2R 开启 | 1318.5 | 1371 | 184 | 1229–1579 |
| 新 fast，R2R 关闭 | 1398.5 | 1448.5 | 190 | 1328–1679 |
| 原 small | 964.5 | 1013 | 86 | 919–1200 |
| 新 small | 927 | 973 | 86 | 886–1215 |

测试机器为 Intel Core i5-8265U（4 核 8 线程），Windows 10 专业版 19045，.NET SDK 9.0.308。启动绝对值有波动：首轮按版本连续测量时，原 fast / small 分别为 582 / 412 ms；交错轮次的相同方案变为 949 / 964.5 ms。因此以同轮对照和完整逐次数据判断取舍，不将这些数值解释为所有机器的固定启动时间。

启用单文件压缩后，fast 在诊断点的工作集从约 78 MiB 增至 184 MiB，就绪中位数增加 369.5 ms。磁盘体积降低是本轮收益，内存与启动耗时是明确的代价；small 工作集保持约 86 MiB。未把执行成功描述为启动或内存全面改善。

按发布文件分类，各部分占比如下。fast 的 .NET 运行时、主程序集和三方托管库均位于 exe 中；small 使用已安装的 .NET 9 Desktop Runtime。

| 部分 | fast MiB | fast 占比 | small MiB | small 占比 |
|---|---:|---:|---:|---:|
| exe | 57.42 | 54.68% | 0.18 | 0.37% |
| OCR 模型、词表与说明 | 23.04 | 21.94% | 23.04 | 46.77% |
| ONNX 原生推理库 | 15.42 | 14.69% | 15.42 | 31.31% |
| WPF 原生组件 | 8.00 | 7.62% | 系统运行时提供 | — |
| PDF 托管库 | 包含于 exe | — | 6.96 | 14.14% |
| 其余程序集、WebP 工具、资源与许可 | 1.13 | 1.07% | 3.66 | 7.41% |

small 的 OCR 模型与 ONNX 推理库合计约 38.46 MiB，占 78.08%。没有减少、量化或替换任何模型。

轻量 OCR 的流程和参数按本机 RapidOcrNet 4.1.0 DLL 核对，参考版本提交为 `e3f71c97ed106fec71aadb38a603a9382e444995`。算法来源为 BobLd/RapidOcrNet 与 RapidAI/RapidOCR（Apache-2.0）；本轮将图像处理改写为 C# BGRA32，保留相应署名和完整许可引用。GitHub 查询遇到 TLS 错误，未宣称已在线确认维护活跃度。直接依赖仍为已缓存的 OnnxRuntime 1.29.0 与 Clipper2 2.0.0，并补齐其许可与第三方通知。

| 项目 | 保留的参数与行为 |
|---|---|
| PPOCRv6 选项 | PythonCompat：padding=0，ImgResize=0，MinSideLen=30，MaxSideLen=2000，LimitSideLen=736，宽高比阈值 8 |
| 图像缩放 | 先约束原图并按 32 对齐；超宽图补上下白边；检测短边不足 736 时放大，尺寸再按 32 对齐 |
| 输入顺序、归一化 | BGRA 的前三通道直接作为 BGR；三个模型使用 mean/std=127.5；Mitchell 三次采样 |
| DBNet | 二值阈值 0.3，3×3 膨胀，外轮廓追踪与多边形简化，最小外接矩形，框分数阈值 0.5，UnClipRatio=1.6，扩框使用 Clipper2 Round/Polygon |
| 裁剪与方向 | 透视裁剪，竖框高/宽≥1.5 时顺时针旋转 90°，分类概率≥0.9 时旋转 180° |
| Cls 输入 | 实测模型元数据为动态批次×3×80×160；保持比例缩放，右侧填充值 128，读取模型尺寸而非硬编码 |
| Rec 输入 | 动态批次×3×48×动态宽度；按 48 高缩放、取整宽度，不补成固定 320 宽；CTC greedy 去重、跳过 blank，保留词表尾部空格；平均字符分数阈值 0.5 |
| 取消 | 每次 ONNX Run 使用独立 RunOptions.Terminate；结束后才释放共用引擎识别门闩 |

原生 WinRT 层仅声明所需 ABI：Language、OcrEngine、SoftwareBitmap、SpeechSynthesizer、向量、HSTRING 和异步接口。通过 SafeHandle 管理独立 COM 引用，明确 Close/Release；像素经 LockBuffer 与 IMemoryBufferByteAccess 直接写入，并校验原生容量与实际平面 stride。所有异步 OCR、合成和读流工作留在 MTA 线程，取消后等待原生完成回调再释放输入。委托使用 IUnknown 前缀和 SDK 中的精确泛型 IID，以 TaskCompletionSource 传递完成，避免释放同步对象与回调返回竞争。

MP4 改用 Media Foundation IMFSinkWriter 逐帧写 H.264，输入 RGB32 并保留 FlipRows。视频尺寸仍向下对齐到 4 的倍数，时间戳按 100ns 单位计算；音频在同一 sink 中从 PCM 输入写入 AAC，不再二次转码混流。WAV 分块读取，整数 8/24/32 位 PCM 转换为 16 位；音频初始化或混流失败会记录日志并重试纯视频。取消保留原最终文件并清理旁路，未生成残缺视频。第一帧解码探针使用 IMFSourceReader，优先按 IMF2DBuffer 顶行指针与实际 stride 取像素。

主工程和自测工程均改为 `net9.0-windows`；当前 Release 构建各为 0 错误、0 警告。发布文件清单不含 RapidOcrNet.dll、SkiaSharp.dll、libSkiaSharp.dll、Microsoft.Windows.SDK.NET.dll、WinRT.Runtime.dll 或任何 .lib。静态库过滤在 ComputeResolvedFilesToPublishList 后、单文件打包和复制分桶前执行。

保留 PDFsharp 带出的 Charting、BarCodes、Snippets、Quality 等子程序集，未手改 .deps.json，也未激进删除托管库。其体积收益较小；实际 small 发布文件已覆盖 PDF 解析、中文重排、跨页、字体与拒绝条件回归。

自测按改动路径运行，未使用旧目标框架 DLL 验证新代码。源码自测使用原有 portable.txt；实际 small 测试仅复制自测宿主，被测 FlashTrans 和其所有依赖都来自临时 small 发布文件。AudioCapture.IsAvailable 单独核验为 True，系统声音测试没有因设备缺失提前返回；本机存在 8 条系统语音记录。

| 验证 | 源码 Release 通过/失败 | 实际 small 发布文件通过/失败 |
|---|---:|---:|
| --only-ocr | 71 / 0 | 71 / 0 |
| --only-record | 73 / 0 | 73 / 0 |
| --only-documents | 31 / 0 | 31 / 0 |
| --only-redaction | 13 / 0 | 未重复运行 |
| --only-startup | 10 / 0 | --benchmark 另测 |
| --only-media | 6 / 0 | 6 / 0 |
| 合计检查执行次数（包含重复的通用检查） | 204 / 0 | 181 / 0 |

--only-media 复用现有 UiProbe 语音语言匹配、真实合成播放与 16 个连续替换请求，以及 AudioMp4Probe 的真实扬声器回环捕获和 AAC 音轨核验。新增录制回归只覆盖替换实现中的具体风险：MP4 已创建旁路后的取消、旧文件保护，以及坏 WAV 的纯视频回退。

RapidRoundTrip、4 条日文/圈号、6 条公式均通过现有真值断言；比较仅忽略空白和折叠全角 ASCII，不代表原排版逐像素一致。打码中 Rapid / Windows × 英文 / 中文四条真实识别路径各识别 3 块、遮挡 2 块，并通过脱敏后复识别。GIF / WebP / MP4 真实编码、方向、1366×766 尺寸、取消及清理均通过。

Windows PowerShell 的 ReleaseScriptsProbe.ps1 已运行通过四组验证：构建失败不覆盖/不关闭现有实例；原位覆盖并解除实际占用，同时保留数据与用户文件；同版本 ZIP 覆盖和清单打包；哈希及目录越界拒绝。该探针构建步骤使用模拟产物，进程占用解除、复制、恢复、ZIP 与校验走真实脚本；实际 dotnet publish 另有上述完整实测。

未运行全量自测、--net 外部翻译请求或公开 OCR 语料下载。Windows 系统 OCR 实际语言包为 en-US、zh-Hans-CN；韩语、俄语、阿拉伯语、泰语仅验证路由与文本规则，缺少语言包的真实识别明确未验证。平台验证为 Windows 10 x64，其他 Windows 版本及架构未实测。

修改文件如下（新增模型引擎和 ABI 文件之外，未重构其他功能模块）：

| 文件 | 修改目的 |
|---|---|
| src/FlashTrans/FlashTrans.csproj | 替换依赖、目标框架、冷路径 R2R 排除与静态库过滤 |
| src/FlashTrans/App.xaml.cs | 更新 benchmark 冷路径检查及说明 |
| src/FlashTrans/Services/RapidOcrEngine.cs | 新增本地 PP-OCRv6 引擎 |
| src/FlashTrans/Services/OcrService.Rapid.cs | 去掉 RapidOcrNet、SkiaSharp，接入新引擎 |
| src/FlashTrans/Services/OcrService.cs | 接入原生 Windows OCR |
| src/FlashTrans/Services/SpeechService.cs | 接入原生系统语音，保留播放与替换逻辑 |
| src/FlashTrans/Services/Mp4Encoder.cs | Media Foundation 视频和 AAC 音频写出 |
| src/FlashTrans/Interop/NativeWinRt.cs | COM、HSTRING、apartment 和异步生命周期 |
| src/FlashTrans/Interop/NativeOcr.cs | Language、SoftwareBitmap、OCR ABI |
| src/FlashTrans/Interop/NativeSpeech.cs | 系统语音元数据、合成与读流 |
| src/FlashTrans/Interop/MediaFoundation.cs | 编码、媒体属性、样本和第一帧解码 ABI |
| src/FlashTrans/Assets/licenses/RapidOcrNet.txt | 算法来源、修改说明与 Apache 许可署名 |
| src/FlashTrans/Assets/licenses/Clipper2.txt | Clipper2 的 Boost 许可 |
| src/FlashTrans/Assets/licenses/OnnxRuntime.txt | OnnxRuntime 的 MIT 许可 |
| src/FlashTrans/Assets/licenses/OnnxRuntime-ThirdPartyNotices.txt | 原始第三方通知 |
| tests/FlashTrans.SelfTest/FlashTrans.SelfTest.csproj | 跟随目标框架，保留 portable.txt 与模型拷贝 |
| tests/FlashTrans.SelfTest/FlipCheck.cs | 原生解码第一帧，解除 WinRT 投影 |
| tests/FlashTrans.SelfTest/UiProbe.cs | 语音元数据改用原生层，复用语音探针 |
| tests/FlashTrans.SelfTest/Program.cs | 增加定向 --only-media 入口 |
| tests/FlashTrans.SelfTest/RecordProbe.cs | 增加 MP4 取消与坏音频回退回归 |
| tests/FlashTrans.SelfTest/ReleaseScriptsProbe.ps1 | 更新构建输出路径 |
| tools/publish.ps1 | 单文件压缩与禁用发布调试符号 |
| tests/FlashTrans.SelfTest/SLIMMING-STAGE1.md | 本验证记录 |
| tests/FlashTrans.SelfTest/SLIMMING-STAGE1.json | 原始尺寸、启动逐次数据、模型哈希与文件清单 |

以下源文件以及三个临时发布方案中的对应文件逐个核验 SHA256，均保持一致；原 PaddleOCR.txt 和 tests/FlashTrans.SelfTest/portable.txt 保留。

| 文件 | SHA256 |
|---|---|
| PP-OCRv6_det_tiny.onnx | F42C0FBD294D95EAC1A550E131B277DAC97462C8025FA4B6C3CEC1B7894BD3D5 |
| ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx | 54379AE5174D026780215FC748A7F31910DEE36818E63D49E17DC598ECC82DF7 |
| PP-OCRv6_rec_small.onnx | 6F327246B50388F3C176AE304BD95767EA6DC0C9AE92153EF8CBE210B3C14884 |
| ppocrv6_small_dict.txt | B5F2BFE2BDD9448429E3E82B51C789775D9B42F2403D082B00662EB77E401C5D |
| README.txt | B95E8B8A44513A7C01F3025B7B73B42F295C7E5BD3BE5B053995576004ADFA67 |

全部 FlashTrans-slim-check-* 临时发布目录和 ZIP 已按路径核验后清理，现有 dist 发布程序仍为原文件。本轮交付为工作区源码修改与验证记录；用户无需更改配置。
