namespace AstroJint.AspNetCore;

/// <summary>
/// Options of the output cache of on-demand pages (<c>AddAstroOutputCache()</c>). Which responses are cached, and for how
/// long, is decided by the <c>Cache-Control</c> header that the page sets; these options say where the copies live and
/// what the cache key includes.
/// </summary>
public sealed class AstroOutputCacheOptions
{
    /// <summary>
    /// Folder that holds the cached responses, one file each, so that they survive a restart of the host and can be
    /// shared by its instances. A relative path is resolved against the content root. <see langword="null"/> (the
    /// default) keeps them in memory instead.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>
    /// Request headers whose values are part of the cache key, in addition to the path and the whole query string.
    /// A response whose <c>Vary</c> header names a request header that is not listed here is not cached, as the host
    /// could not tell its variants apart. Empty by default.
    /// </summary>
    public IList<string> VaryByHeaders { get; } = [];

    /// <summary>
    /// How often the folder of <see cref="Directory"/> is searched for files whose response has expired, which are
    /// deleted. An expired file is also deleted when it is requested. <see cref="TimeSpan.Zero"/> turns the sweep off.
    /// Defaults to 10 minutes.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(10);
}
