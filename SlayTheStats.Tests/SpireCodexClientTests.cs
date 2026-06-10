using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStats.Community;
using Xunit;

namespace SlayTheStats.Tests;

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/> — matches request URLs against a
/// queue of canned responses so the client's parse/paginate/error logic is tested
/// without real network. A matching factory may be invoked more than once (for
/// pagination), keyed by a substring of the relative URL.
/// </summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<string, HttpResponseMessage> _responder;
    public List<string> Requests { get; } = new();

    public FakeHandler(Func<string, HttpResponseMessage> responder) => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);
        return Task.FromResult(_responder(url));
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}

public class SpireCodexClientTests
{
    private static SpireCodexClient Client(FakeHandler h) => new("https://example.test", "SlayTheStats/test", h);

    [Fact]
    public async Task GetMetrics_ParsesRowsAndNullableFields()
    {
        const string body = """
        { "cohort":"all","entity_type":"potions","total_runs":217212,"baseline_win_rate":42.5,
          "rows":[ {"id":"GIGANTIFICATION_POTION","upgraded":false,"win_rate":39.9,"pick_rate":null,
                    "picks":2429,"wins":970,"losses":1459,"offered":0,"picked":0,
                    "pick_rate_by_act":[null,null,null]} ] }
        """;
        var client = Client(new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.OK, body)));

        var r = await client.GetMetricsAsync("potions", "all", CancellationToken.None);

        Assert.True(r.IsOk);
        Assert.Equal(217212, r.Value!.TotalRuns);
        Assert.Equal(42.5, r.Value.BaselineWinRate);
        var row = Assert.Single(r.Value.Rows);
        Assert.Equal("GIGANTIFICATION_POTION", row.Id);
        Assert.Equal(39.9, row.WinRate);
        Assert.Null(row.PickRate);
        Assert.Equal(3, row.PickRateByAct.Count);
        Assert.All(row.PickRateByAct, v => Assert.Null(v));
    }

    [Fact]
    public async Task GetMetrics_BuildsCohortQuery()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.OK, "{\"rows\":[]}"));
        var client = Client(handler);
        await client.GetMetricsAsync("cards", "a10", CancellationToken.None);
        Assert.Contains("metrics/cards?cohort=a10", handler.Requests[0]);
    }

    [Fact]
    public async Task Http429_ReturnsError_WithRetryAfter()
    {
        var resp = new HttpResponseMessage((HttpStatusCode)429);
        resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        var client = Client(new FakeHandler(_ => resp));

        var r = await client.GetVersionsAsync(CancellationToken.None);

        Assert.False(r.IsOk);
        Assert.Equal(TimeSpan.FromSeconds(120), r.RetryAfter);
    }

    [Fact]
    public async Task Http500_ReturnsError()
    {
        var client = Client(new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.InternalServerError, "Internal Server Error")));
        var r = await client.GetVersionsAsync(CancellationToken.None);
        Assert.False(r.IsOk);
        Assert.Contains("500", r.Error);
    }

    [Fact]
    public async Task NetworkException_ReturnsError_DoesNotThrow()
    {
        var client = Client(new FakeHandler(_ => throw new HttpRequestException("connection refused")));
        var r = await client.GetVersionsAsync(CancellationToken.None);
        Assert.False(r.IsOk);
        Assert.Contains("connection refused", r.Error);
    }

    [Fact]
    public async Task EncounterStats_PagesUntilHasNextFalse_Concatenates()
    {
        var page1 = """{ "encounters":[{"encounter_id":"A","act":1,"room_type":"monster","total":1,"fatal":0}], "total":2, "limit":1, "page":1, "has_next":true }""";
        var page2 = """{ "encounters":[{"encounter_id":"B","act":2,"room_type":"elite","total":1,"fatal":0}], "total":2, "limit":1, "page":2, "has_next":false }""";
        var handler = new FakeHandler(url =>
            FakeHandler.Json(HttpStatusCode.OK, url.Contains("page=2") ? page2 : page1));
        var client = Client(handler);

        var r = await client.GetEncounterStatsAsync(CancellationToken.None);

        Assert.True(r.IsOk);
        Assert.Equal(2, r.Value!.Count);
        Assert.Equal("A", r.Value[0].EncounterId);
        Assert.Equal("B", r.Value[1].EncounterId);
        Assert.Equal(2, handler.Requests.Count); // stopped after has_next=false
    }

    [Fact]
    public async Task EncounterStats_PropagatesPageError()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.InternalServerError, "boom"));
        var client = Client(handler);
        var r = await client.GetEncounterStatsAsync(CancellationToken.None);
        Assert.False(r.IsOk);
    }
}
