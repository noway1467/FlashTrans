using System.Runtime.InteropServices;
using FlashTrans.Core;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using Windows.Storage;

namespace FlashTrans.Services;

/// <summary>
/// 录制系统音频（扬声器输出）。
///
/// 走 Windows.Media.Audio.AudioGraph，它能从 AudioDeviceOutputNode 拿到正在播放的声音
/// （loopback，跟你听到的一样），输出到 AudioFileOutputNode 存成文件。
/// 这套 API 只在 Win10 1607+ 可用，而项目的最低版本是 17763，所以不用再判。
///
/// 为什么不走 WASAPI loopback：那条要自己管 IAudioClient/IAudioCaptureClient，
/// 轮询拿缓冲、写进文件、混音都得自己做，而 AudioGraph 这套全是托管 API。
/// </summary>
public sealed class AudioCapture : IDisposable
{
    AudioGraph? _graph;
    AudioDeviceInputNode? _inputNode;
    AudioFileOutputNode? _outputNode;
    string? _outputPath;
    bool _muted;
    bool _paused;

    /// <summary>
    /// 这台机器支不支持音频录制。系统缺驱动、音频服务没跑都可能是 false。
    /// 只是粗判断，真正算不算数还是看 StartAsync 成不成。
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                // 试着拿一下默认渲染设备，拿不到就说明没有音频输出设备
                var id = Windows.Media.Devices.MediaDevice.GetDefaultAudioRenderId(
                    Windows.Media.Devices.AudioDeviceRole.Default);
                return !string.IsNullOrEmpty(id);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 开始录制系统音频到 outputPath（.m4a）。
    /// 返回 null 表示成功，返回字符串表示失败原因（直接拿去给用户看）。
    /// </summary>
    public async Task<string?> StartAsync(string outputPath)
    {
        _outputPath = outputPath;

        try
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency,
            };
            var result = await AudioGraph.CreateAsync(settings);
            if (result.Status != AudioGraphCreationStatus.Success)
                return $"无法创建音频图：{result.Status}";

            _graph = result.Graph;

            // loopback：拿系统正在播放的声音（扬声器输出）
            var inputResult = await _graph.CreateDeviceInputNodeAsync(MediaCategory.Media);
            if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                Dispose();
                return $"无法打开音频输入：{inputResult.Status}";
            }
            _inputNode = inputResult.DeviceInputNode;

            // 输出到文件，用 AAC 编码（.m4a）
            var dir = System.IO.Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            var folder = await StorageFolder.GetFolderFromPathAsync(dir ?? ".");
            var file = await folder.CreateFileAsync(
                System.IO.Path.GetFileName(outputPath), CreationCollisionOption.ReplaceExisting);

            var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
            var outputResult = await _graph.CreateFileOutputNodeAsync(file, profile);
            if (outputResult.Status != AudioFileNodeCreationStatus.Success)
            {
                Dispose();
                return $"无法创建音频输出：{outputResult.Status}";
            }
            _outputNode = outputResult.FileOutputNode;

            _inputNode.AddOutgoingConnection(_outputNode);
            _graph.Start();
            return null;
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070490)
        {
            // ERROR_NOT_FOUND：系统上没有音频设备或驱动
            Dispose();
            return "找不到音频设备（没有扬声器或驱动未安装）";
        }
        catch (Exception ex)
        {
            Dispose();
            return "音频录制启动失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 静音 / 取消静音。静音期间照样往文件里写，写的是无声——
    /// 不能直接停掉节点，那样音频会比视频短一截，静音之后的部分全部音画错位。
    /// </summary>
    public void SetMuted(bool muted)
    {
        if (_muted == muted) return;
        _muted = muted;
        try
        {
            if (_inputNode is not null) _inputNode.OutgoingGain = muted ? 0.0 : 1.0;
        }
        catch (Exception ex)
        {
            Log.Warn("切换静音失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 暂停 / 恢复。这个跟静音相反，要真的停住：视频那边暂停期间不抓帧，
    /// 音频再走下去就比视频长，恢复之后的声音全部对不上画面。
    /// </summary>
    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        try
        {
            if (paused) _graph?.Stop();
            else _graph?.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("切换音频暂停失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 停止录制并最终化输出文件。必须调一次，不然文件是残的。
    /// 返回 null 表示成功，返回字符串表示失败（但文件可能已经写了一部分）。
    /// </summary>
    public async Task<string?> StopAsync()
    {
        try
        {
            _graph?.Stop();
            if (_outputNode is not null)
                await _outputNode.FinalizeAsync();
            return null;
        }
        catch (Exception ex)
        {
            return "音频录制停止失败：" + ex.Message;
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        _outputNode?.Dispose();
        _outputNode = null;
        _inputNode?.Dispose();
        _inputNode = null;
        _graph?.Dispose();
        _graph = null;
    }
}
