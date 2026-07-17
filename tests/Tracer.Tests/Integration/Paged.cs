using System.Net.Http.Json;

namespace Tracer.Tests.Integration;

/// <summary>
/// The paged envelope every bounded list endpoint returns, for tests that only
/// care about the rows. Mirrors <c>Tracer.Api.Contracts.PagedResult&lt;T&gt;</c>
/// by property name, so <see cref="System.Net.Http.Json"/> binds it directly.
/// </summary>
public sealed record Paged<T>(List<T> Items, int Page, int PageSize, int Total, int TotalPages);

public static class PagedHttpClientExtensions
{
    /// <summary>
    /// Reads a paged list endpoint and hands back just the rows, so a test that
    /// does not care about the envelope reads a list exactly as it did before
    /// these endpoints were paged.
    /// </summary>
    public static async Task<List<T>> GetListAsync<T>(this HttpClient client, string url) =>
        (await client.GetFromJsonAsync<Paged<T>>(url))!.Items;
}
