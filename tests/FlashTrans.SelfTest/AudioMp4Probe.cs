using System;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Threading.Tasks;
using FlashTrans.Core;
using FlashTrans.Interop;
using FlashTrans.Services;

namespace FlashTrans.SelfTest;

static class AudioMp4Probe
{
    public static void Run()
    {
        if (!AudioCapture.IsAvailable || !Mp4Encoder.Available) return;

        var dir = Path.Combine(Path.GetTempPath(), "FlashTrans.audioprobe." + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var tone = Path.Combine(dir, "tone.wav");
            var audio = Path.Combine(dir, "captured.wav");
            WriteTone(tone);
            var frames = WriteFrames(dir, 10);

            using (var capture = new AudioCapture())
            {
                var start = capture.StartAsync(audio).GetAwaiter().GetResult();
                Need(start is null, "系统声音启动失败：" + start);
                using var player = new SoundPlayer(tone);
                player.Play();
                Task.Delay(1500).GetAwaiter().GetResult();
                var stop = capture.StopAsync().GetAwaiter().GetResult();
                Need(stop is null, "系统声音停止失败：" + stop);
            }

            var captured = File.ReadAllBytes(audio);
            Need(captured.Length > 44 && BitConverter.ToInt32(captured, 40) > 0,
                "系统声音文件没有有效 PCM 数据");
            Need(captured.Skip(44).Any(value => value != 0), "系统声音文件全是静音");

            var result = Task.Run(() => AnimEncoder.SaveAsync(
                frames, Path.Combine(dir, "out"), 10, RecordFormat.Mp4, audio))
                .GetAwaiter().GetResult();
            Need(result.Format == RecordFormat.Mp4, "带系统声音的 MP4 编码失败：" + result.FellBackWhy);
            var mp4 = File.ReadAllBytes(result.Path);
            Need(Encoding.ASCII.GetString(mp4).Contains("soun", StringComparison.Ordinal),
                "最终 MP4 没有音频轨道");
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    static string[] WriteFrames(string dir, int count)
    {
        const int width = 48;
        const int height = 32;
        var paths = new string[count];
        for (var index = 0; index < count; index++)
        {
            var pixels = new byte[width * height * 4];
            for (var pixel = 0; pixel < pixels.Length; pixel += 4)
            {
                pixels[pixel] = 0x40;
                pixels[pixel + 1] = (byte)(0x60 + index * 4);
                pixels[pixel + 2] = 0xC0;
                pixels[pixel + 3] = 0xFF;
            }
            paths[index] = Path.Combine(dir, $"f{index:D5}.png");
            new CapturedImage(width, height, pixels).SavePng(paths[index]);
        }
        return paths;
    }

    static void WriteTone(string path)
    {
        const int rate = 48000;
        const short channels = 2;
        var data = new byte[rate * 2 * channels * 2];
        for (var frame = 0; frame < rate * 2; frame++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * frame / rate) * 12000);
            for (var channel = 0; channel < channels; channel++)
                BitConverter.GetBytes(sample).CopyTo(data, (frame * channels + channel) * 2);
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + data.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(rate);
        writer.Write(rate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(data.Length);
        writer.Write(data);
    }

    static void Need(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}