namespace SLCopilot.Core;

/// <summary>
/// 发送模型请求并对暂时性失败退避重试：连接失败，以及 408、429、500、502、503、504、529 状态码。
/// 只在拿到成功响应之前重试，因此不会重复接收或计入已生成的输出；其他状态码立即抛出 <see cref="ProviderHttpException"/>。
/// 优先遵守服务端 Retry-After（上限 20 秒），否则按 1、2、4 秒退避，最多尝试 4 次。取消立即生效。
/// </summary>
internal static class ProviderRetry
{
    public const int MaxAttempts = 4;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(20);
    private static readonly HashSet<int> Retryable = [408, 429, 500, 502, 503, 504, 529];

    /// <summary>测试可替换的等待实现。</summary>
    internal static Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <param name="createRequest">每次尝试都创建新的请求（请求内容不能重复发送）。</param>
    /// <param name="onRetry">重试前回调，参数为第几次重试与等待时间；在调用线程上同步执行。</param>
    /// <param name="maxAttempts">最多尝试次数；行内补全传 1（不重试，过时的补全没有价值）。</param>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, Func<HttpRequestMessage> createRequest,
        Action<int, TimeSpan>? onRetry, CancellationToken cancellationToken, int maxAttempts = MaxAttempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            using (var request = createRequest())
            {
                try
                {
                    response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
                {
                    await WaitAsync(attempt, null, onRetry, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode;
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            response.Dispose();
            if (attempt >= maxAttempts || !Retryable.Contains((int)status))
                throw new ProviderHttpException(status, $"Provider returned HTTP {(int)status}.");
            await WaitAsync(attempt, retryAfter, onRetry, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task WaitAsync(int attempt, TimeSpan? retryAfter, Action<int, TimeSpan>? onRetry, CancellationToken cancellationToken)
    {
        var delay = retryAfter is { } value && value > TimeSpan.Zero
            ? (value < MaxRetryAfter ? value : MaxRetryAfter)
            : TimeSpan.FromSeconds(1 << (attempt - 1));
        onRetry?.Invoke(attempt, delay);
        return Delay(delay, cancellationToken);
    }
}
