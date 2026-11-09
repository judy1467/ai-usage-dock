using System;
using System.Threading;
using System.Threading.Tasks;
using AiUsageDock.Core.Models;

namespace AiUsageDock.Core.Abstractions
{
    /// <summary>
    /// A provider that can fetch usage information from a remote service.
    /// </summary>
    public interface IUsageProvider
    {
        string Id { get; }

        TimeSpan DefaultInterval { get; }

        Task<ProviderResult> FetchAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Persists provider secrets (API keys) for the application.
    /// </summary>
    public interface ISecretStore
    {
        string? GetDeepSeekApiKey();

        void SetDeepSeekApiKey(string? apiKey);

        void Clear();
    }

    /// <summary>
    /// A discovered Antigravity language-server endpoint.
    /// </summary>
    public sealed record AntigravityEndpoint(int ProcessId, int Port, string CsrfToken);

    /// <summary>
    /// Result of attempting to discover a running Antigravity language server.
    /// </summary>
    public sealed record AntigravityDiscoveryResult(
        AntigravityEndpoint? Endpoint,
        string? ErrorCode = null,
        string? ErrorDetail = null)
    {
        public bool IsSuccess => Endpoint is not null;
    }

    /// <summary>
    /// Locates a running Antigravity language server and its CSRF token.
    /// </summary>
    public interface IAntigravityDiscovery
    {
        Task<AntigravityDiscoveryResult> DiscoverAsync(CancellationToken ct = default);
    }
}
