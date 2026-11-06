using System.Net;
using Microsoft.Extensions.Logging;

namespace Chronicle.Plugin.TheTVDB.Tests;

/// <summary>A handler that answers from a queue and remembers every request it was sent.</summary>
internal sealed class StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] answers) : HttpMessageHandler
{
    private int _i;
    public List<HttpRequestMessage> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var a = answers[Math.Min(_i++, answers.Length - 1)];
        return Task.FromResult(a(request));
    }
}

/// <summary>A logger that keeps everything written to it, so a test can prove what never reached a log.</summary>
internal sealed class CaptureLogger : ILogger
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
}

internal static class Reply
{
    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);
}
