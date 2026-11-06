using System.Net;
using Chronicle.Plugins;

namespace Chronicle.Plugin.TheTVDB.Tests;

public class ClientTests
{
    private const string Key = "SECRET-KEY-1234567890";
    private const string Login = "{\"status\":\"success\",\"data\":{\"token\":\"TOKEN-ABCDEFG\"}}";

    private static TheTvdbClient Client(StubHandler h, CaptureLogger? log = null) => new(new HttpClient(h), Key, log ?? new CaptureLogger());

    [Fact]
    public async Task A_rejected_key_is_one_login_call_and_an_auth_exception_that_does_not_echo_the_key()
    {
        var h = new StubHandler(_ => Reply.Status(HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<PluginAuthException>(() => Client(h).SearchSeriesAsync("x", default));
        Assert.DoesNotContain(Key, ex.Message);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task A_token_that_keeps_being_refused_is_an_auth_exception_not_a_loop()
    {
        var h = new StubHandler(_ => Reply.Json(Login), _ => Reply.Status(HttpStatusCode.Unauthorized),
                                _ => Reply.Json(Login), _ => Reply.Status(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<PluginAuthException>(() => Client(h).SearchSeriesAsync("x", default));
        Assert.Equal(4, h.Requests.Count);
    }

    [Fact]
    public async Task A_failing_call_leaves_the_key_out_of_the_exception_and_the_log()
    {
        var log = new CaptureLogger();
        var h = new StubHandler(_ => Reply.Json(Login), _ => Reply.Status(HttpStatusCode.InternalServerError));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Client(h, log).SearchSeriesAsync("x", default));
        Assert.DoesNotContain(Key, ex.ToString());
        Assert.DoesNotContain(log.Lines, l => l.Contains(Key) || l.Contains("TOKEN-ABCDEFG"));
    }

    [Fact]
    public async Task A_search_answer_is_read_and_sent_with_the_bearer_token()
    {
        var h = new StubHandler(_ => Reply.Json(Login),
            _ => Reply.Json("{\"status\":\"success\",\"data\":[{\"tvdb_id\":\"1\",\"name\":\"Bonding\"}]}"));
        var r = await Client(h).SearchSeriesAsync("bonding", default);
        Assert.Equal("Bonding", Assert.Single(r!).Name);
        Assert.Equal("Bearer", h.Requests[1].Headers.Authorization?.Scheme);
    }
}
