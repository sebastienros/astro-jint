using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace AstroJint.AspNetCore;

/// <summary>
/// Caches an on-demand response for as long as its own <c>Cache-Control</c> header allows, the way a shared cache in
/// front of the host would (RFC 9111): a <c>GET</c> or <c>HEAD</c> of an anonymous visitor, answered with a cacheable
/// status, an <c>s-maxage</c> or a <c>max-age</c>, and neither <c>no-store</c>, <c>no-cache</c>, <c>private</c> nor
/// a <c>Set-Cookie</c>. A page opts in with <c>Astro.response.headers.set('Cache-Control', 'public, max-age=60')</c>.
/// </summary>
/// <remarks>
/// <c>MapAstro()</c> attaches the policy to the on-demand endpoint, where it does nothing until the application adds
/// the output caching middleware. The expiration has to be known when the response starts, which is before the
/// middleware asks the policy about the response, so the endpoint also calls <see cref="OnResponseStarting"/> once the
/// headers of the page are set and before its body is written.
/// </remarks>
internal sealed class AstroOutputCachePolicy : IOutputCachePolicy
{
    private AstroOutputCacheOptions? _options;

    ValueTask IOutputCachePolicy.CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var attempt = IsCacheableRequest(context.HttpContext);
        context.EnableOutputCaching = true;
        context.AllowCacheLookup = attempt;
        context.AllowCacheStorage = attempt;
        context.AllowLocking = true;
        context.CacheVaryByRules.QueryKeys = "*";

        var options = _options ??= context.HttpContext.RequestServices.GetService<IOptions<AstroOutputCacheOptions>>()?.Value ?? new();
        if (options.VaryByHeaders.Count > 0)
        {
            context.CacheVaryByRules.HeaderNames = new StringValues([.. options.VaryByHeaders]);
        }

        return ValueTask.CompletedTask;
    }

    ValueTask IOutputCachePolicy.ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask IOutputCachePolicy.ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        Apply(context);
        return ValueTask.CompletedTask;
    }

    /// <summary>Decides, from the headers of the response that is about to be written, whether and how long it is cached.</summary>
    public static void OnResponseStarting(HttpContext context)
    {
        if (context.Features.Get<IOutputCacheFeature>() is { Context: { } cacheContext })
        {
            Apply(cacheContext);
        }
    }

    /// <summary>
    /// The time a shared cache may serve a response with this <c>Cache-Control</c>: <c>s-maxage</c>, else <c>max-age</c>,
    /// unless a directive forbids storing it. False when it must not be cached, which includes the absence of the header.
    /// </summary>
    internal static bool TryGetLifetime(StringValues cacheControl, out TimeSpan lifetime)
    {
        lifetime = TimeSpan.Zero;
        if (StringValues.IsNullOrEmpty(cacheControl) || !CacheControlHeaderValue.TryParse(cacheControl.ToString(), out var directives))
        {
            return false;
        }

        if (directives.NoStore || directives.NoCache || directives.Private)
        {
            return false;
        }

        var age = directives.SharedMaxAge ?? directives.MaxAge;
        if (age is not { } value || value <= TimeSpan.Zero)
        {
            return false;
        }

        lifetime = value;
        return true;
    }

    private static bool IsCacheableRequest(HttpContext context)
    {
        var request = context.Request;
        return (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && StringValues.IsNullOrEmpty(request.Headers.Authorization)
            && !IsAuthenticated(context);
    }

    private static bool IsAuthenticated(HttpContext context) => context.User.Identity?.IsAuthenticated == true;

    private static void Apply(OutputCacheContext context)
    {
        if (!context.AllowCacheStorage)
        {
            return;
        }

        var httpContext = context.HttpContext;
        var response = httpContext.Response;
        if (!IsCacheableStatus(response.StatusCode)
            || !StringValues.IsNullOrEmpty(response.Headers.SetCookie)
            || IsAuthenticated(httpContext)
            || !TryGetLifetime(response.Headers.CacheControl, out var lifetime)
            || !CanVary(response.Headers.Vary, context.CacheVaryByRules.HeaderNames))
        {
            context.AllowCacheStorage = false;
            return;
        }

        context.ResponseExpirationTimeSpan = lifetime;
    }

    // The statuses that a cache may store without the response saying more (RFC 9110, section 15.1), minus the ones
    // that a site would never want reused.
    private static bool IsCacheableStatus(int status) =>
        status is StatusCodes.Status200OK or StatusCodes.Status203NonAuthoritative or StatusCodes.Status204NoContent
            or StatusCodes.Status300MultipleChoices or StatusCodes.Status301MovedPermanently or StatusCodes.Status308PermanentRedirect
            or StatusCodes.Status404NotFound or StatusCodes.Status405MethodNotAllowed or StatusCodes.Status410Gone or StatusCodes.Status414UriTooLong;

    // A response can only be cached when every request header it varies on is part of the cache key.
    private static bool CanVary(StringValues vary, StringValues keyed)
    {
        if (StringValues.IsNullOrEmpty(vary))
        {
            return true;
        }

        foreach (var value in vary)
        {
            foreach (var name in (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (name == "*" || !Contains(keyed, name))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool Contains(StringValues names, string name)
    {
        foreach (var candidate in names)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
