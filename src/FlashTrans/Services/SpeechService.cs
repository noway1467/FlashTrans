using System.IO;
using System.Media;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.SpeechSynthesis;

namespace FlashTrans.Services;

/// <summary>Windows 本机语音合成。只使用已安装的系统语音，不联网。</summary>
public static class SpeechService
{
    static readonly object Gate = new();
    static SoundPlayer? _player;
    static MemoryStream? _audio;
    static CancellationTokenSource? _cancel;
    static int _generation;

    /// <summary>指定语言是否有可用的系统语音。</summary>
    public static bool HasVoice(string? language)
    {
        try { return FindVoice(language) is not null; }
        catch (Exception ex)
        {
            Log.Warn("枚举系统语音失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>合成并播放文本；再次播放会停止上一次播放。</summary>
    public static async Task<string?> SpeakAsync(string text, string? language)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        VoiceInformation? voice;
        try { voice = FindVoice(language); }
        catch (Exception ex)
        {
            Log.Warn("选择系统语音失败：" + ex.Message);
            return "系统语音不可用";
        }
        if (voice is null) return "没有安装对应语言的系统语音";

        CancellationTokenSource cancel;
        int generation;
        lock (Gate)
        {
            StopLocked();
            cancel = new CancellationTokenSource();
            _cancel = cancel;
            generation = ++_generation;
        }

        SoundPlayer? player = null;
        MemoryStream? audio = null;
        try
        {
            using var synth = new SpeechSynthesizer { Voice = voice };
            using var stream = await synth.SynthesizeTextToStreamAsync(text)
                .AsTask(cancel.Token).ConfigureAwait(false);
            using var input = stream.AsStreamForRead();
            audio = new MemoryStream();
            await input.CopyToAsync(audio, cancel.Token).ConfigureAwait(false);
            audio.Position = 0;

            player = new SoundPlayer(audio);
            player.Load();

            lock (Gate)
            {
                if (generation != _generation || cancel.IsCancellationRequested)
                {
                    return null;
                }
                // 播放与替换必须在同一把锁内，否则新请求可能先释放这个播放器。
                player.Play();
                _player = player;
                _audio = audio;
                player = null;
                audio = null;
            }
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Warn("语音播放失败：" + ex.Message);
            return "语音播放失败";
        }
        finally
        {
            player?.Dispose();
            audio?.Dispose();
            lock (Gate)
            {
                if (ReferenceEquals(_cancel, cancel))
                {
                    _cancel = null;
                }
                // 取消只发信号；由持有它的请求在异步操作退出后释放。
                cancel.Dispose();
            }
        }
    }

    static VoiceInformation? FindVoice(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return SpeechSynthesizer.DefaultVoice;
        var voices = SpeechSynthesizer.AllVoices;
        var index = SelectVoiceIndex(language, voices.Select(v => v.Language).ToArray());
        return index < 0 ? null : voices[index];
    }

    internal static int SelectVoiceIndex(string language, IReadOnlyList<string> available)
    {
        var want = language.Trim().Replace('_', '-');
        // 先保留精确地区选择，再同时归一化两端；不能只改请求端而漏掉 zh-CN 语音。
        for (var i = 0; i < available.Count; i++)
            if (available[i].Equals(want, StringComparison.OrdinalIgnoreCase)) return i;
        want = NormalizeLanguageTag(want);
        for (var i = 0; i < available.Count; i++)
        {
            var candidate = NormalizeLanguageTag(available[i]);
            if (candidate.Equals(want, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(want + "-", StringComparison.OrdinalIgnoreCase) ||
                want.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    static string NormalizeLanguageTag(string language)
    {
        var want = language.Trim().Replace('_', '-');
        return want.ToLowerInvariant() switch
        {
            // 应用使用 Google 风格代码，Windows 语音使用 ISO 639-1 + Script。
            "zh-cn" => "zh-Hans",
            "zh-tw" => "zh-Hant",
            "tl" => "fil",
            _ => want,
        };
    }

    static void StopLocked()
    {
        _generation++;
        try { _cancel?.Cancel(); } catch { /* 语音停止失败不影响下一次播放 */ }
        _cancel = null;
        try { _player?.Stop(); } catch { }
        _player?.Dispose();
        _player = null;
        _audio?.Dispose();
        _audio = null;
    }

    public static void Stop()
    {
        lock (Gate) StopLocked();
    }
}
