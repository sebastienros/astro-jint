# Spike S5: output caching of on-demand pages

**Question.** An on-demand page costs what the interpreter costs, and a visitor who asks for the
same page twice pays it twice. Node.js compiles the bundle once and runs it with a JIT, so the same
page is several times cheaper there. How far behind is the host, and what does caching the rendered
output buy, in the process and in files that survive a restart?

**Short answer.**

- A warm engine renders the sample's pages in **0.8 to 1.1 ms** through Kestrel; Node.js with the
  official adapter renders them in **0.16 to 0.19 ms**, 5 to 6 times faster. The API route, which
  renders nothing, costs about the same in both (0.25 ms against 0.14 ms).
- A page served from the **output cache** costs **0.10 to 0.13 ms**, the same as a prerendered file,
  and the host serves 29,000 of them per second instead of 7,000 to 10,000 renders. A file-backed
  entry costs 25 microseconds more than an in-memory one at one connection and nothing under load.
- **After a restart** a page in the file cache answers in 10 ms without an engine; a page that is not
  cached waits 390 ms for the first engine of the process (100 ms with warm-up on, which is the
  JIT compiling the render path).
- The pool now hands out the **most recently returned engine** instead of the one idle for longest.
  At one connection that is 15 to 25 percent lower latency, because a few engines stay warm instead of
  every engine paying the first-render cost of every page in turn; under load it changes nothing.
- Which pages are cached is the page's own decision, through `Cache-Control`, the same signal a CDN
  would read. The sample's product pages set `public, max-age=60` and are cached; `/time`,
  `/session` and `/me` set nothing and render for every request.

## Method

Machine: Apple M4 Pro (14 cores), 48 GB, macOS 15.8, .NET runtime 10.0.11, Jint `5.0.0-preview-2249`,
Astro 7.3.8, Node.js 24.4. The sample site, `samples/hybrid`, in Release, Server GC, 14 engines, warm-up
on. The same sources were built a second time with `@astrojs/node` (standalone mode) and started with
`node dist/server/entry.mjs`.

- Latency and throughput with [bombardier](https://github.com/codesenberg/bombardier) 2.0.2 over
  loopback, keep-alive, 6 seconds per run, 1, 14 and 64 connections. p50 is the median latency of one
  request.
- The first request of a fresh process with `curl`, which adds about a millisecond of its own
  (a prerendered page measures 1.0 ms through curl and 0.10 ms through bombardier).
- The load generator shares the machine with the server. Run-to-run noise is 10 to 20 percent for
  the on-demand scenarios; the FIFO/LIFO comparison was run interleaved, three rounds each.

## 1. Jint against Node.js

Median latency (p50) and requests per second, same site, same pages:

| Page | Jint, 1 connection | Node.js, 1 connection | Jint, 14 connections | Node.js, 14 connections |
| --- | ---: | ---: | ---: | ---: |
| `/time` (on-demand page) | 0.84 ms, 1,050 req/s | 0.16 ms, 5,600 req/s | 1.1-1.8 ms, 6,800-10,500 req/s | 1.4 ms, 9,200 req/s |
| `/products/1` (on-demand page, not cached) | 1.0 ms, 590 req/s | 0.17 ms, 5,400 req/s | 1.1 ms, 10,800 req/s | 1.4 ms, 9,300 req/s |
| `/api/time` (on-demand API route) | 0.25 ms, 3,700 req/s | 0.14 ms, 7,000 req/s | 0.55 ms, 20,000 req/s | 1.0 ms, 13,000 req/s |
| `/blog` (prerendered) | 0.10 ms, 9,800 req/s | 0.14 ms, 6,900 req/s | 0.48 ms, 26,000 req/s | 0.86 ms, 16,000 req/s |

So per request, a page costs 5 to 6 times more in the interpreter than in V8, which is the "10x"
that a sequential comparison shows once curl's own overhead is in both numbers. Node.js is one thread,
so with 14 engines the host answers as many pages per second as the single Node.js process, and more
API routes; a Node.js cluster of 14 would be far ahead.

The first requests of a process are slower still. With warm-up on, the first page of a fresh host
costs 95 to 100 ms (the .NET JIT compiling Jint's and Astro's render path at tier 0), the next ones 3
to 15 ms while the methods are rejitted, and the steady state above is reached after a few hundred
requests. Without warm-up the first page costs 370 to 390 ms, which is the engine being created.
Node.js answers its first page in 14 ms and its second in 2 ms.

## 2. The output cache

`AddAstroOutputCache()` adds ASP.NET Core's output caching with a policy that caches an on-demand
response for as long as its `Cache-Control` header allows, like a shared cache in front of the host
(RFC 9111): `s-maxage`, else `max-age`; never `no-store`, `no-cache`, `private`, a `Set-Cookie`, a
request with `Authorization` or an authenticated user, or a status that a cache may not reuse. The key
is the method, the path, the whole query string and the request headers of `VaryByHeaders`; a response
whose `Vary` names another header is not cached. The entry lives in memory, or in a folder
(`Directory`) as one file per entry.

`/products/1` sets `Cache-Control: public, max-age=60`:

| `/products/1` | 1 connection | 14 connections | 64 connections |
| --- | ---: | ---: | ---: |
| Rendered (cache off) | 1.01 ms, 590 req/s | 1.05 ms, 10,800 req/s | 4.8 ms, 11,000 req/s |
| Memory cache | 0.10 ms, 9,200 req/s | 0.48 ms, 28,800 req/s | 2.2 ms, 28,600 req/s |
| File cache | 0.13 ms, 7,300 req/s | 0.48 ms, 28,600 req/s | 2.2 ms, 29,100 req/s |
| Prerendered page, for comparison | 0.10 ms, 9,800 req/s | 0.48 ms, 26,000 req/s | 2.2 ms, 26,700 req/s |

A cache hit costs what a prerendered file costs: 10 times less latency than a render and 2.7 times
the throughput, which is Kestrel's limit on this machine, not the cache's. The file store reads one
small file per hit (the entries are a few KB), which adds 25 microseconds at one connection and is
hidden by the parallelism at 14.

The first request still pays for the render: with the cache on, `/products/1` costs 93 ms on a fresh
host and 1.5 ms from the third request on. After a restart, the file cache answers the page in 9.9 ms
(the JIT again) while a page that has to render waits 390 ms for the first engine, so a site whose
pages are cached in files recovers from a deploy without a slow first visit per page.

The cache does not help a page that cannot be cached. In the sample that is most of them: `/time`
shows the time, `/session` counts visits, `/me` depends on the user, `/contact` handles a form. Those
pay the interpreter on every request, and the way to make them cheaper is to prerender what does not
need the request, or to cache a fragment of the data they use on the .NET side.

## 3. The engine pool hands out the warmest engine

The pool kept its idle engines in a FIFO channel, so sequential requests went round all 14 engines,
and each engine rendered every page for the first time in turn (module evaluation of the page's
chunk, Jint's property caches, cold processor caches). It now keeps a stack: the engine returned last
is handed out first. Interleaved runs of the two variants, 5 seconds each, fresh process each time:

| Round | `/time`, 1 connection, FIFO | LIFO | `/time`, 14 connections, FIFO | LIFO | `/api/time`, 1 connection, FIFO | LIFO |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1.40 ms | 1.08 ms | 1.88 ms | 1.77 ms | 0.34 ms | 0.25 ms |
| 2 | 1.30 ms | 1.19 ms | 1.83 ms | 2.25 ms | 0.30 ms | 0.27 ms |
| 3 | 3.65 ms (noise) | 1.10 ms | 3.83 ms (noise) | 1.75 ms | 1.07 ms (noise) | 0.24 ms |

At one connection the stack is 15 to 25 percent faster; at 14 connections every engine is busy and
the order does not matter. Waiters are still served in the order they arrived.

## 4. What was not done

- **ClearScript (V8).** It would bring the render cost down to the Node.js column, and
  `Microsoft.ClearScript.V8` ships native V8 for win-x64/x86/arm64, linux-x64/arm64/arm and
  osx-x64/arm64. But Jint also provides what the bundle runs on: the fetch-handler hosting, `Request`,
  `Response`, `Headers`, `URL`, `TextEncoder`, streams, `crypto.subtle`, timers and the memory and time
  limits per request. V8 has none of these: they would have to be written as host objects or bundled
  as polyfills, the sandbox (`FetchUrlFilter`, `MemoryLimitBytes`, a cancellable request) rebuilt on
  V8's own mechanisms, and the host would carry a 30 MB native library per platform and lose
  trimming and AOT. That is a second engine behind the same pool, not a swap, and a separate spike.
- **Caching fragments** (a product list, a CMS call) on the .NET side with `HybridCache`, exposed to
  the page through `Astro.locals`, which would help the pages that this cache cannot.
- **`IOutputCacheStore` on Redis** for a multi-instance host; the file store covers instances that
  share a folder.
