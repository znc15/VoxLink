using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VoxLink.Services;

// 直接驱动引擎核心链路（不经 UI），测量：
// 1) startSession 到就绪（Whisper base 加载 + processor 复用）
// 2) listLocalModels 首次/二次（哈希缓存效果）
// 3) HY-MT 首句/后续句延迟（预热 + context 2048 效果）
var serializer = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true
};

// 标记输出同时写文件：llama.cpp 原生日志与控制台缓冲交错时会丢行
using var markFile = new StreamWriter(
    Path.Combine(Path.GetTempPath(), "perf_marks.txt"), append: false);
static TimeSpan Mark(Stopwatch sw, string label, StreamWriter? file = null)
{
    var line = $"[{sw.ElapsedMilliseconds,6} ms] {label}";
    Console.WriteLine(line);
    file?.WriteLine(line);
    file?.Flush();
    return sw.Elapsed;
}
static void Note(string text, StreamWriter file)
{
    Console.WriteLine(text);
    file.WriteLine(text);
    file.Flush();
}

// --- A. LocalModelManager 校验缓存 ---
Console.WriteLine("== A. LocalModelManager GetStatus 全目录（首次 = 全量哈希）==");
var swA = Stopwatch.StartNew();
var manager = new LocalModelManager();
foreach (var definition in manager.List())
{
    _ = manager.GetStatus(definition.Id);
}
var first = Mark(swA, "首次 GetStatus 全目录", markFile);
swA.Restart();
foreach (var definition in manager.List())
{
    _ = manager.GetStatus(definition.Id);
}
var second = Mark(swA, "二次 GetStatus 全目录（应命中缓存）", markFile);
Note($"缓存收益：{first.TotalMilliseconds - second.TotalMilliseconds:F0} ms", markFile);
await manager.DisposeAsync();

// --- B. 翻译会话（本地 Whisper base + HY-MT）---
Console.WriteLine();
Console.WriteLine("== B. TranslationSession（本地 Whisper base + HY-MT，麦克风采集）==");
using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
var factory = new TranslationServiceFactory(httpClient, new LocalModelManager());
var session = new TranslationSession(
    new AsrRecognizerFactory(httpClient),
    factory,
    new HybridTextToSpeechService(httpClient));
var settings = new VoxLink.Models.AppSettings
{
    CaptureMicrophone = true,
    CaptureSystemAudio = false,
    
    AsrProvider = VoxLink.Models.AsrProvider.LocalWhisper,
    AsrProtocol = VoxLink.Models.AsrProtocol.LocalWhisper,
    WhisperModel = "base",
    
    TranslationProvider = VoxLink.Models.TranslationProvider.LocalHyMtGguf,
    SpeakMyTranslation = false,
    EnableTranslationRefinement = false,
    MyLanguageCode = "zh",
    OtherLanguageCode = "en"
};
var swB = Stopwatch.StartNew();
session.StatusChanged += (_, e) => Mark(swB, $"状态: {e.Message} ({e.Activity})", markFile);
await session.StartAsync(settings);
Mark(swB, "startAsync 返回（会话就绪）", markFile);

// --- C. HY-MT 翻译延迟（预热后）---
Console.WriteLine();
Console.WriteLine("== C. HY-MT typed 翻译延迟（先等预载完成，模拟用户稍后开口）==");
await Task.Delay(3000);
for (var index = 0; index < 4; index++)
{
    var swC = Stopwatch.StartNew();
    var message = await session.TranslateTypedTextAsync(
        index switch
        {
            0 => "你好，世界！",
            1 => "这个游戏太难了，我打不过那个Boss。",
            2 => "稍等一下，我去买瓶水。",
            _ => "太棒了，我们终于赢了这一局！"
        },
        settings);
    Note($"  句{index + 1}: {swC.ElapsedMilliseconds,5} ms → {message.TranslatedText}", markFile);
    await Task.Delay(200);
}

await session.StopAsync();
Console.WriteLine();
Console.WriteLine("完成。");
