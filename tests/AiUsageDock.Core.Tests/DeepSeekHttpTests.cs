using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AiUsageDock.Core.Abstractions;
using AiUsageDock.Core.Models;
using AiUsageDock.Core.Providers.DeepSeek;
using Xunit;

namespace AiUsageDock.Core.Tests;

public class DeepSeekHttpTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private int _calls;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("https://api.deepseek.com/user/balance", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
        }
    }

    private sealed class KeyStore : ISecretStore
    {
        private string? _key;

        public KeyStore(string? key) => _key = key;

        public string? GetDeepSeekApiKey() => _key;

        public void SetDeepSeekApiKey(string? apiKey) => _key = apiKey;

        public void Clear() => _key = null;
    }

    [Fact]
    public async Task Fetch_401_ReturnsAuthFailed_AndDoesNotThrow()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, """{"error":"bad key"}""");
        var adapter = new DeepSeekAdapter(new HttpClient(handler), new KeyStore("sk-test"));

        var result = await adapter.FetchAsync();

        Assert.Equal(ProviderHealth.AuthFailed, result.Health);
        Assert.Equal("http_401", result.ErrorCode);
        Assert.Null(result.Snapshot);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Fetch_200_ParsesBalance()
    {
        const string body = """
        {"is_available":true,"balance_infos":[{"currency":"USD","total_balance":"12.34","granted_balance":"2.34","topped_up_balance":"10.00"}]}
        """;
        var adapter = new DeepSeekAdapter(new HttpClient(new StubHandler(HttpStatusCode.OK, body)), new KeyStore("sk-test"));

        var result = await adapter.FetchAsync();

        Assert.Equal(ProviderHealth.Ok, result.Health);
        var snapshot = result.Snapshot!.DeepSeek!;
        Assert.Equal(12.34m, snapshot.Primary.Total);
        Assert.Equal("$12.34", snapshot.Primary.FormatTotal());
        Assert.True(snapshot.IsAvailable);
    }

    [Fact]
    public async Task Fetch_WithoutKey_ReturnsKeyMissing_AndSkipsHttp()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var adapter = new DeepSeekAdapter(new HttpClient(handler), new KeyStore(null));

        var result = await adapter.FetchAsync();

        Assert.Equal(ProviderHealth.AuthFailed, result.Health);
        Assert.Equal("key_missing", result.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }
}
