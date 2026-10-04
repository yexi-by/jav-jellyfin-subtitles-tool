using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Jellyfin.Plugin.SubtitlesTool.Core;

/// <summary>仅筛选供 LLM 核对的候选，不以向量相似度确认对应关系。</summary>
internal sealed class SubtitleCandidates : IDisposable
{
    private readonly InferenceSession _model;
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly IReadOnlyList<SubtitleCue> _cues;
    private readonly List<float[]> _vectors = [];

    public SubtitleCandidates(string assets, IReadOnlyList<SubtitleCue> cues)
    {
        using var stream = File.OpenRead(Path.Combine(assets, "sentencepiece.bpe.model"));
        _tokenizer = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
        using var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 2, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
        _model = new InferenceSession(Path.Combine(assets, "embedding.onnx"), options);
        _cues = cues;
    }

    public IReadOnlyList<SubtitleCue> Select(SpeechText speech, CancellationToken token, long? proposedOffset = null)
    {
        if (proposedOffset is { } offset)
        {
            // 已有对应只用于缩小候选范围，另一处仍需独立核对具体台词。
            var nearby = _cues.Where(cue => cue.EndMilliseconds >= speech.Words[0].StartMilliseconds - offset - 15000
                && cue.StartMilliseconds <= speech.Words[^1].EndMilliseconds - offset + 15000).Take(36).ToArray();
            if (nearby.Length > 0) return nearby;
        }
        if (_vectors.Count == 0)
            for (var index = 0; index < _cues.Count; index += 8)
            {
                token.ThrowIfCancellationRequested();
                _vectors.AddRange(Encode(_cues.Skip(index).Take(8).Select(cue => cue.Text).ToArray()));
            }
        var queries = SpeechRecognizer.Phrases(speech).Select(phrase => phrase.Text).Where(text => AnchorMatcher.Normalize(text).Length >= 6).OrderByDescending(text => AnchorMatcher.Normalize(text).Length).Take(3).ToArray();
        if (queries.Length == 0) return [];
        var selected = new HashSet<int>();
        foreach (var query in Encode(queries))
            foreach (var index in _vectors.Select((vector, index) => (Score: vector.Zip(query).Sum(pair => pair.First * pair.Second), Index: index))
                .Where(pair => _cues[pair.Index].EndMilliseconds >= speech.Words[0].StartMilliseconds - AnchorMatcher.MaximumOffsetMilliseconds
                    && _cues[pair.Index].StartMilliseconds <= speech.Words[^1].EndMilliseconds + AnchorMatcher.MaximumOffsetMilliseconds)
                .OrderByDescending(pair => pair.Score).Take(4).Select(pair => pair.Index))
                for (var nearby = Math.Max(0, index - 1); nearby <= Math.Min(_cues.Count - 1, index + 1); nearby++) selected.Add(nearby);
        return selected.Order().Select(index => _cues[index]).ToArray();
    }

    private float[][] Encode(string[] texts)
    {
        var ids = texts.Select(TokenIds).ToArray();
        var width = ids.Max(row => row.Length);
        var input = Enumerable.Repeat(1L, texts.Length * width).ToArray();
        var mask = new long[input.Length]; var types = new long[input.Length];
        for (var row = 0; row < ids.Length; row++)
            for (var col = 0; col < ids[row].Length; col++) { input[row * width + col] = ids[row][col]; mask[row * width + col] = 1; }
        using var output = _model.Run([
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(input, [texts.Length, width])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [texts.Length, width])),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(types, [texts.Length, width]))]);
        var hidden = output.First().AsTensor<float>();
        var result = new float[texts.Length][];
        for (var row = 0; row < texts.Length; row++)
        {
            var vector = new float[hidden.Dimensions[2]];
            for (var col = 0; col < ids[row].Length; col++)
                for (var dim = 0; dim < vector.Length; dim++) vector[dim] += hidden[row, col, dim] / ids[row].Length;
            var norm = Math.Sqrt(vector.Sum(value => value * value));
            result[row] = vector.Select(value => (float)(value / Math.Max(norm, 1e-12))).ToArray();
        }
        return result;
    }

    internal long[] TokenIds(string text) => new long[] { 0 }.Concat(_tokenizer.EncodeToIds("query: " + text, false, false, 94, out _, out _).Select(id => id == 0 ? 3L : id + 1L)).Append(2).ToArray();
    public void Dispose() => _model.Dispose();
}
