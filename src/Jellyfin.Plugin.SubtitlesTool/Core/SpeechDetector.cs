using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

/// <summary>切分音频并跳过静音；检测到人声不作为字幕对应证据。</summary>
public sealed class SpeechDetector : IDisposable
{
    private readonly InferenceSession _session;
    private static readonly object RuntimeGate = new();
    private static IntPtr _runtime;
    internal static void LoadRuntime(string cache)
    {
        lock (RuntimeGate)
        {
            if (_runtime != IntPtr.Zero) return;
            var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
            var file = "onnxruntime-1.22.1" + (OperatingSystem.IsWindows() ? ".dll" : ".so");
            var path = Path.Combine(cache, file);
            Directory.CreateDirectory(cache);
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("SubtitlesTool.ort." + rid)
                ?? throw new ToolException("安装包缺少 CPU 人声检测运行组件。");
            using var data = new MemoryStream(); resource.CopyTo(data);
            var bytes = data.ToArray();
            if (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes))) File.WriteAllBytes(path, bytes);
            _runtime = NativeLibrary.Load(path);
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, _, _) => name == "onnxruntime" ? _runtime : IntPtr.Zero);
            }
            catch (InvalidOperationException) { /* 已注册的 ONNX Runtime 解析器负责其原生库。 */ }
        }
    }

    public SpeechDetector(string cache)
    {
        LoadRuntime(cache);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("SubtitlesTool.VoiceModel")
            ?? throw new ToolException("安装包缺少人声检测组件，请重新安装插件。");
        using var data = new MemoryStream(); resource.CopyTo(data);
        using var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
        _session = new InferenceSession(data.ToArray(), options);
    }

    public async Task<SubtitleCue[]> DetectAsync(string pcmPath, long positionMilliseconds, CancellationToken token)
    {
        var pcm = await File.ReadAllBytesAsync(pcmPath, token);
        if (pcm.Length > 32 * 1024 * 1024 || pcm.Length % 2 != 0) throw new ToolException("音频片段大小或采样格式异常。");
        var input = new float[576]; var state = new float[256];
        var probabilities = new List<float>();
        for (var sample = 0; sample < pcm.Length / 2; sample += 512)
        {
            token.ThrowIfCancellationRequested();
            Array.Clear(input, 64, 512);
            var count = Math.Min(512, pcm.Length / 2 - sample);
            for (var index = 0; index < count; index++) input[index + 64] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan((sample + index) * 2, 2)) / 32768f;
            using var results = _session.Run([
                NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, [1, 576])),
                NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(state, [2, 1, 128])),
                NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { 16000 }, Array.Empty<int>()))]);
            probabilities.Add(results.First(value => value.Name == "output").AsTensor<float>().First());
            state = results.First(value => value.Name == "stateN").AsTensor<float>().ToArray();
            Array.Copy(input, 512, input, 0, 64);
        }
        return Segments(probabilities, positionMilliseconds, pcm.Length / 32);
    }

    public static SubtitleCue[] Segments(IReadOnlyList<float> probabilities, long position, long duration)
    {
        var result = new List<SubtitleCue>();
        long? start = null; long? silence = null;
        for (var frame = 0; frame <= probabilities.Count; frame++)
        {
            var probability = frame == probabilities.Count ? 0 : probabilities[frame];
            var time = Math.Min(duration, frame * 32L);
            if (probability >= .5)
            {
                start ??= time;
                silence = null;
            }
            else if (start is not null && probability < .35)
            {
                silence ??= time;
                if (time - silence.Value >= 100 || frame == probabilities.Count)
                {
                    if (silence.Value - start.Value >= 250)
                        result.Add(new(result.Count, position + Math.Max(0, start.Value - 30), position + Math.Min(duration, silence.Value + 30), ""));
                    start = null; silence = null;
                }
            }
        }
        return result.ToArray();
    }

    public void Dispose() => _session.Dispose();
}
