using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace AstroJint.AspNetCore.Tests;

/// <summary>The output cache of on-demand pages: what is cached is decided by the Cache-Control header of the page.</summary>
public class AstroOutputCacheTests
{
    private const string Cacheable = "/cached?cc=public,%20max-age=60";

    private static Task<TestSite> StartCachingSiteAsync(Action<AstroOutputCacheOptions>? configure = null, DistFolder? dist = null) =>
        TestSite.StartAsync(
            dist,
            configureServices: services => services.AddAstroOutputCache(configure),
            configureApp: app => app.UseOutputCache());

    private static async Task<Rendered> GetAsync(HttpClient client, string path, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        configure?.Invoke(request);
        using var response = await client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var json = document.RootElement;
        return new Rendered(
            response.StatusCode,
            json.GetProperty("engine").GetString()!,
            json.GetProperty("handled").GetInt32(),
            json.GetProperty("lang").GetString(),
            response.Headers.Age);
    }

    private sealed record Rendered(HttpStatusCode Status, string Engine, int Handled, string? Language, TimeSpan? Age);

    [Theory]
    [InlineData("public, max-age=60", 60)]
    [InlineData("max-age=60", 60)]
    [InlineData("MAX-AGE=5", 5)]
    [InlineData("s-maxage=10, max-age=60", 10)]
    [InlineData("public, max-age=60, s-maxage=10", 10)]
    [InlineData("public, max-age=60, must-revalidate", 60)]
    public void TryGetLifetime_ReadsSharedMaxAgeThenMaxAge(string header, int seconds)
    {
        Assert.True(AstroOutputCachePolicy.TryGetLifetime(new StringValues(header), out var lifetime));
        Assert.Equal(TimeSpan.FromSeconds(seconds), lifetime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("public")]
    [InlineData("public, max-age=0")]
    [InlineData("no-store")]
    [InlineData("max-age=60, no-store")]
    [InlineData("no-cache, max-age=60")]
    [InlineData("private, max-age=60")]
    [InlineData("private, s-maxage=60")]
    [InlineData("max-age=nonsense")]
    public void TryGetLifetime_RefusesWhatASharedCacheMayNotStore(string header)
    {
        Assert.False(AstroOutputCachePolicy.TryGetLifetime(new StringValues(header), out _));
    }

    [Fact]
    public async Task AResponseWithMaxAgeIsServedFromTheCache()
    {
        await using var site = await StartCachingSiteAsync();

        var first = await GetAsync(site.Client, Cacheable);
        var second = await GetAsync(site.Client, Cacheable);

        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(first.Engine, second.Engine);
        Assert.Equal(first.Handled, second.Handled);
    }

    [Fact]
    public async Task AResponseWithoutCacheControlIsRenderedEveryTime()
    {
        await using var site = await StartCachingSiteAsync();

        var first = await GetAsync(site.Client, "/cached");
        var second = await GetAsync(site.Client, "/cached");

        Assert.NotEqual((first.Engine, first.Handled), (second.Engine, second.Handled));
    }

    [Theory]
    [InlineData("/cached?cc=no-store")]
    [InlineData("/cached?cc=private,%20max-age=60")]
    [InlineData("/cached?cc=public,%20max-age=60&cookie=session=1;%20Path=/")]
    [InlineData("/cached?cc=public,%20max-age=60&code=500")]
    [InlineData("/cached?cc=public,%20max-age=60&code=302")]
    [InlineData("/cached?cc=public,%20max-age=60&vary=Accept-Language")]
    [InlineData("/cached?cc=public,%20max-age=60&vary=*")]
    public async Task AResponseThatMustNotBeStoredIsRenderedEveryTime(string path)
    {
        await using var site = await StartCachingSiteAsync();

        var first = await GetAsync(site.Client, path);
        var second = await GetAsync(site.Client, path);

        Assert.NotEqual((first.Engine, first.Handled), (second.Engine, second.Handled));
    }

    [Fact]
    public async Task ACacheableNotFoundIsCached()
    {
        await using var site = await StartCachingSiteAsync();
        const string path = "/cached?cc=public,%20max-age=60&code=404";

        var first = await GetAsync(site.Client, path);
        var second = await GetAsync(site.Client, path);

        Assert.Equal(HttpStatusCode.NotFound, first.Status);
        Assert.Equal((first.Engine, first.Handled), (second.Engine, second.Handled));
    }

    [Fact]
    public async Task ARequestWithAuthorizationIsNeitherServedFromNorStoredInTheCache()
    {
        await using var site = await StartCachingSiteAsync();
        var cached = await GetAsync(site.Client, Cacheable);

        var authorized = await GetAsync(site.Client, Cacheable, request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token"));
        var authorizedAgain = await GetAsync(site.Client, Cacheable, request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token"));
        var anonymous = await GetAsync(site.Client, Cacheable);

        Assert.NotEqual((cached.Engine, cached.Handled), (authorized.Engine, authorized.Handled));
        Assert.NotEqual((authorized.Engine, authorized.Handled), (authorizedAgain.Engine, authorizedAgain.Handled));
        Assert.Equal((cached.Engine, cached.Handled), (anonymous.Engine, anonymous.Handled));
    }

    [Fact]
    public async Task APostIsNotCached()
    {
        await using var site = await StartCachingSiteAsync();

        using var first = await site.Client.PostAsync(Cacheable, new StringContent("a"));
        using var second = await site.Client.PostAsync(Cacheable, new StringContent("a"));
        using var json1 = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var json2 = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

        Assert.NotEqual(
            (json1.RootElement.GetProperty("engine").GetString(), json1.RootElement.GetProperty("handled").GetInt32()),
            (json2.RootElement.GetProperty("engine").GetString(), json2.RootElement.GetProperty("handled").GetInt32()));
    }

    [Fact]
    public async Task TheQueryStringIsPartOfTheKey()
    {
        await using var site = await StartCachingSiteAsync();

        var one = await GetAsync(site.Client, Cacheable + "&x=1");
        var two = await GetAsync(site.Client, Cacheable + "&x=2");
        var oneAgain = await GetAsync(site.Client, Cacheable + "&x=1");

        Assert.NotEqual((one.Engine, one.Handled), (two.Engine, two.Handled));
        Assert.Equal((one.Engine, one.Handled), (oneAgain.Engine, oneAgain.Handled));
    }

    [Fact]
    public async Task AResponseThatVariesOnAKeyedHeaderIsCachedPerValue()
    {
        await using var site = await StartCachingSiteAsync(options => options.VaryByHeaders.Add("Accept-Language"));
        const string path = Cacheable + "&vary=accept-language";

        var en = await GetAsync(site.Client, path, request => request.Headers.Add("Accept-Language", "en"));
        var fr = await GetAsync(site.Client, path, request => request.Headers.Add("Accept-Language", "fr"));
        var enAgain = await GetAsync(site.Client, path, request => request.Headers.Add("Accept-Language", "en"));

        Assert.Equal("en", en.Language);
        Assert.Equal("fr", fr.Language);
        Assert.NotEqual((en.Engine, en.Handled), (fr.Engine, fr.Handled));
        Assert.Equal((en.Engine, en.Handled), (enAgain.Engine, enAgain.Handled));
    }

    [Fact]
    public async Task ACachedResponseExpires()
    {
        await using var site = await StartCachingSiteAsync();
        const string path = "/cached?cc=max-age=1";

        var first = await GetAsync(site.Client, path);
        var second = await GetAsync(site.Client, path);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var third = await GetAsync(site.Client, path);

        Assert.Equal((first.Engine, first.Handled), (second.Engine, second.Handled));
        Assert.NotEqual((first.Engine, first.Handled), (third.Engine, third.Handled));
    }

    [Fact]
    public async Task WithoutTheMiddlewareNothingIsCached()
    {
        await using var site = await TestSite.StartAsync(configureServices: services => services.AddAstroOutputCache());

        var first = await GetAsync(site.Client, Cacheable);
        var second = await GetAsync(site.Client, Cacheable);

        Assert.NotEqual((first.Engine, first.Handled), (second.Engine, second.Handled));
    }

    [Fact]
    public async Task TheFileStoreKeepsAResponseAcrossHosts()
    {
        using var dist = DistFolder.CreateDefault();
        var directory = Path.Combine(dist.Root, "cache");
        Rendered first;

        await using (var site = await StartCachingSiteAsync(options => options.Directory = "cache", dist))
        {
            first = await GetAsync(site.Client, Cacheable);
            var again = await GetAsync(site.Client, Cacheable);

            Assert.Equal((first.Engine, first.Handled), (again.Engine, again.Handled));
            Assert.Single(Directory.EnumerateFiles(directory, "*.cache"));
        }

        // A new host has new engines, so a fresh render would come from another engine id.
        await using (var site = await StartCachingSiteAsync(options => options.Directory = "cache", dist))
        {
            var restored = await GetAsync(site.Client, Cacheable);
            var fresh = await GetAsync(site.Client, "/cached");

            Assert.Equal((first.Engine, first.Handled), (restored.Engine, restored.Handled));
            Assert.NotEqual(first.Engine, fresh.Engine);
        }
    }
}
