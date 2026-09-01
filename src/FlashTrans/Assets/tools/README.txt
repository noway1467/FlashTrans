img2webp.exe —— 录制动图存 WebP 时调的编码器。

这是 libwebp 的官方命令行工具，随包发布，放在这儿就能用，不用装什么东西。

来源与校验
----------
libwebp 1.6.0，Windows x64，取自 Google 官方发布页：
    https://developers.google.com/speed/webp/download
    -> https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.6.0-windows-x64.zip

  zip          4,106,264 字节
               SHA256 48886f506b21f62e4661f0f4cbfca19800897c385128e8902542d29a950c93f1
  img2webp.exe   775,680 字节
               SHA256 b26bfabad2607fd307283cd7c6cf6115251dc44e9462492f6b401a69c109e252
  自报版本     WebP Encoder 1.6.0 / WebP Mux 1.6.0 / libsharpyuv 0.4.2

官方发布包旁边有 .asc 签名。要真正验证来源得先有 Google 的公钥；只对签名文件
自身算哈希只能说明下载没损坏，说明不了来源可信。

为什么要外挂一个程序
--------------------
WPF 和 WIC 里没有 WebP 编码器（系统从 Win10 1809 起只带解码器），而动图 WebP
还要写 VP8X / ANIM / ANMF 这几个容器块，WIC 的编码器接口没有对应的写法。
详见 Services/AnimEncoder.cs 顶部的说明。

删掉它会怎样
------------
程序照样跑。「设置 → 截图 → 录制动图 → 格式」选 WebP 时会自动退回 GIF，
提示条和设置页那行说明都会讲明现在是哪种情况。GIF 那条路是程序自己编的
（每帧单独编、再拼动图字节流），不碰任何外部程序。

自测里有一项专门把这个 exe 挪走一次，确认退回 GIF 这条路真的通，跑完再放回去。

换一个版本
----------
把新的 img2webp.exe 覆盖到这儿，重新编译即可（csproj 里是
`Assets\tools\*.exe` 通配符，有就拷到输出目录）。上面那些哈希记得跟着更。

许可
----
BSD-3-Clause，全文见同目录的 libwebp-COPYING.txt（取自 webmproject/libwebp
的 v1.6.0 标签）。二次分发要带上它——csproj 里已经配好跟着一起拷。
