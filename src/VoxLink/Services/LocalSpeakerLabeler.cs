using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using SherpaOnnx;
using VoxLink.Audio;

namespace VoxLink.Services;

internal sealed class LocalSpeakerLabeler : IAsyncDisposable
{
    private const string ModelFileName =
        "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx";
    private const string ModelUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/" + ModelFileName;
    private const long ModelSize = 28_281_164;
    private const string ModelSha256 =
        "aa3cfc16963a10586a9393f5035d6d6b57e98d358b347f80c2a30bf4f00ceba2";
    private const double MatchThreshold = 0.65;
    private const int MaximumSpeakers = 8;
    private static readonly HttpClient ModelHttpClient = CreateModelHttpClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<SpeakerCluster> _clusters = [];
    private SpeakerEmbeddingExtractor? _extractor;
    private bool _disposed;

    public event EventHandler<ModelProgressEventArgs>? ModelProgress;

    internal static string ModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoxLink",
        "models",
        "speaker",
        ModelFileName);

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_extractor is not null)
            {
                return;
            }

            if (!await IsModelUsableAsync(cancellationToken).ConfigureAwait(false))
            {
                await DownloadModelAsync(cancellationToken).ConfigureAwait(false);
            }

            ModelProgress?.Invoke(this, new ModelProgressEventArgs("正在加载本地说话人模型…"));
            var config = new SpeakerEmbeddingExtractorConfig
            {
                Model = ModelPath,
                NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                Provider = "cpu",
                Debug = 0
            };
            _extractor = new SpeakerEmbeddingExtractor(config);
            ModelProgress?.Invoke(this, new ModelProgressEventArgs("本地说话人模型已就绪", 1));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SpeakerIdentity?> IdentifyAsync(
        AudioUtterance utterance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        if (utterance.SampleRate != PcmAudioConverter.TargetSampleRate
            || utterance.Duration < TimeSpan.FromMilliseconds(800))
        {
            return null;
        }

        await PrepareAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var extractor = _extractor
                ?? throw new InvalidOperationException("本地说话人模型尚未加载。");
            using var stream = extractor.CreateStream();
            stream.AcceptWaveform(utterance.SampleRate, utterance.Samples);
            stream.InputFinished();
            if (!extractor.IsReady(stream))
            {
                return null;
            }

            var embedding = Normalize(extractor.Compute(stream));
            if (embedding.Length == 0)
            {
                return null;
            }

            var bestIndex = -1;
            var bestSimilarity = double.NegativeInfinity;
            for (var index = 0; index < _clusters.Count; index++)
            {
                var similarity = CosineSimilarity(embedding, _clusters[index].Centroid);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestIndex = index;
                }
            }

            if (bestIndex < 0
                || (bestSimilarity < MatchThreshold && _clusters.Count < MaximumSpeakers))
            {
                bestIndex = _clusters.Count;
                _clusters.Add(new SpeakerCluster(embedding, 1));
            }
            else
            {
                _clusters[bestIndex] = _clusters[bestIndex].Add(embedding);
            }

            return new SpeakerIdentity(
                $"local-{bestIndex}",
                $"说话人 {ToLabel(bestIndex)}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _extractor?.Dispose();
            _extractor = null;
            _clusters.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task DownloadModelAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(ModelPath)
            ?? throw new InvalidOperationException("说话人模型路径无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = ModelPath + ".download";
        TryDelete(temporaryPath);
        ModelProgress?.Invoke(this, new ModelProgressEventArgs(
            "首次启用：正在下载本地说话人模型…",
            0));
        try
        {
            using var response = await ModelHttpClient.GetAsync(
                ModelUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                useAsync: true);
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                if (copied > ModelSize)
                {
                    throw new InvalidDataException("说话人模型大小超过预期。");
                }

                ModelProgress?.Invoke(this, new ModelProgressEventArgs(
                    $"正在下载说话人模型 {copied / 1024 / 1024} MB…",
                    Math.Min(0.99, (double)copied / ModelSize)));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();
            await VerifyModelAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, ModelPath, overwrite: true);
            ModelProgress?.Invoke(this, new ModelProgressEventArgs("说话人模型下载完成", 1));
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static async Task<bool> IsModelUsableAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ModelPath))
        {
            return false;
        }

        try
        {
            await VerifyModelAsync(ModelPath, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (InvalidDataException)
        {
            TryDelete(ModelPath);
            return false;
        }
    }

    private static async Task VerifyModelAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != ModelSize)
        {
            throw new InvalidDataException("说话人模型大小不正确。");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            useAsync: true);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false));
        if (!hash.Equals(ModelSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("说话人模型 SHA-256 不匹配。");
        }
    }

    private static float[] Normalize(float[] embedding)
    {
        var magnitude = Math.Sqrt(embedding.Sum(value => value * value));
        if (magnitude <= double.Epsilon)
        {
            return [];
        }

        for (var index = 0; index < embedding.Length; index++)
        {
            embedding[index] = (float)(embedding[index] / magnitude);
        }

        return embedding;
    }

    private static double CosineSimilarity(float[] left, float[] right)
    {
        if (left.Length != right.Length)
        {
            return double.NegativeInfinity;
        }

        double result = 0;
        for (var index = 0; index < left.Length; index++)
        {
            result += left[index] * right[index];
        }

        return result;
    }

    private static string ToLabel(int index) => index < 26
        ? ((char)('A' + index)).ToString()
        : (index + 1).ToString();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateModelHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VoxLink/1.0");
        return client;
    }

    private sealed record SpeakerCluster(float[] Centroid, int Count)
    {
        public SpeakerCluster Add(float[] embedding)
        {
            var combined = new float[Centroid.Length];
            for (var index = 0; index < combined.Length; index++)
            {
                combined[index] = ((Centroid[index] * Count) + embedding[index]) / (Count + 1);
            }

            return new SpeakerCluster(Normalize(combined), Count + 1);
        }
    }
}

internal sealed record SpeakerIdentity(string Id, string Label);
