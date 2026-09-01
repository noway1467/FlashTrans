这个目录放录制动图要用的 img2webp.exe。

没有它程序照样能录，只是「设置 → 截图 → 录制动图 → 格式」选 WebP 时会存成
GIF（提示条里会说一声）。GIF 那条路是程序自己编的，不依赖任何外部程序。

为什么必须外挂一个程序：WPF 和 WIC 里没有 WebP 编码器（系统从 Win10 1809
起只带解码器），而动图 WebP 还要写 VP8X / ANIM / ANMF 这几个容器块，
WIC 的编码器接口没有对应的写法。参见 Services/AnimEncoder.cs 里的说明。

怎么装
------
1. 从 Google 的官方发布页拿 libwebp 的 Windows 版：
   https://developers.google.com/speed/webp/download
   （直链在 https://storage.googleapis.com/downloads.webmproject.org/releases/webp/ ）
2. 解压，把 bin\img2webp.exe 拷到这个目录，也就是：
       src\FlashTrans\Assets\tools\img2webp.exe
   已发布的程序里则是 exe 旁边的：
       Assets\tools\img2webp.exe
3. 重新编译（csproj 里已经写好了「有就拷过去」的规则），或者直接把 exe
   放进已发布目录的同名位置。

放对了的话，设置页「格式」那一栏底下的说明会从「没找到…」变成「WebP 可用」。

校验
----
官方发布包旁边有 .asc 签名。要真正验证得先有 Google 的公钥，光对签名文件
自身的哈希只能说明下载没损坏，说明不了来源可信 —— 自己判断要不要验到那一步。

许可
----
libwebp 是 BSD-3-Clause，许可证全文见同目录的 libwebp-COPYING.txt
（取自 webmproject/libwebp 的 v1.6.0 标签）。二次分发要带上它。
