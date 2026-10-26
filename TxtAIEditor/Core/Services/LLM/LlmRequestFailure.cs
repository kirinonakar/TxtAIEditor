using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services.LLM
{
    // Preserve HTTP metadata rather than parsing localized provider error messages.
    internal sealed class LlmHttpException : HttpRequestException
    {
        public TimeSpan? RetryAfter { get; }

        public LlmHttpException(HttpResponseMessage response, string message)
            : base(message, null, response.StatusCode)
        {
            var retryAfter = response.Headers.RetryAfter;
            RetryAfter = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
        }
    }

    internal sealed class LlmRequestException : Exception
    {
        public LlmRequestException(string message, Exception? innerException = null)
            : base(message, innerException) { }
    }

    internal static class LlmRequestRetryPolicy
    {
        internal const int MaxRetries = 3;

        internal static bool IsTransient(Exception exception) => exception switch
        {
            HttpRequestException http => http.StatusCode == null ||
                (int)http.StatusCode == 429 || (int)http.StatusCode >= 500,
            IOException => true,
            TimeoutException => true,
            OperationCanceledException => true, // Caller cancellation is checked separately.
            _ => false
        };

        internal static TimeSpan GetDelay(Exception exception, int retry)
        {
            TimeSpan backoff = TimeSpan.FromSeconds(Math.Pow(2, retry)) +
                TimeSpan.FromMilliseconds(Random.Shared.Next(250));
            return exception is LlmHttpException { RetryAfter: { } requested } && requested > backoff
                ? requested
                : backoff;
        }

        internal static async Task<T> ExecuteAsync<T>(
            Func<Task<T>> request,
            Func<bool> hasStartedResponse,
            CancellationToken cancellationToken,
            Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
        {
            delayAsync ??= Task.Delay;
            for (int retry = 0; ; retry++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await request();
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                    !hasStartedResponse() && retry < MaxRetries && IsTransient(ex))
                {
                    await delayAsync(GetDelay(ex, retry), cancellationToken);
                }
            }
        }
    }
}
