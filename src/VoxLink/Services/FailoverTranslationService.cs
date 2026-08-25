using System.IO;
using System.Net.Http;
using System.Text.Json;
using VoxLink.Models;

namespace VoxLink.Services;

public sealed class FailoverTranslationService : ITranslationService
{
    private readonly ITranslationService[] _services;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeSpan _serviceTimeout;

    public FailoverTranslationService(params ITranslationService[] services)
        : this(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(4), services)
    {
    }

    public FailoverTranslationService(
        TimeSpan operationTimeout,
        TimeSpan serviceTimeout,
        params ITranslationService[] services)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(operationTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(serviceTimeout, TimeSpan.Zero);
        _operationTimeout = operationTimeout;
        _serviceTimeout = serviceTimeout;
        _services = services;
    }

    public async Task<string> TranslateAsync(
        string text,
        LanguageOption sourceLanguage,
        LanguageOption targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (_services.Length == 0)
        {
            throw new InvalidOperationException("没有可用的翻译服务。");
        }

        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(_operationTimeout);
        List<Exception>? failures = null;
        foreach (var service in _services)
        {
            using var serviceTimeout = CancellationTokenSource.CreateLinkedTokenSource(operationTimeout.Token);
            serviceTimeout.CancelAfter(_serviceTimeout);
            try
            {
                return await service.TranslateAsync(
                    text,
                    sourceLanguage,
                    targetLanguage,
                    serviceTimeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception) when (operationTimeout.IsCancellationRequested)
            {
                (failures ??= []).Add(exception);
                break;
            }
            catch (OperationCanceledException exception) when (serviceTimeout.IsCancellationRequested)
            {
                (failures ??= []).Add(exception);
            }
            catch (Exception exception) when (exception is HttpRequestException
                or InvalidDataException
                or InvalidOperationException
                or JsonException)
            {
                (failures ??= []).Add(exception);
            }
        }

        throw new InvalidOperationException(
            "免密翻译服务均暂时不可用。请检查网络，或在高级设置中切换到 OpenAI 兼容服务。",
            failures is null ? null : new AggregateException(failures));
    }
}
