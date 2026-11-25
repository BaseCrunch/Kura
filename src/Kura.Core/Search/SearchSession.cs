using Kura.Core.Engines;

namespace Kura.Core.Search;

/// <summary>
/// Paging state for one search (SPEC 1.6): page counter with jump-to-page offset, end detection on an empty
/// page, cumulative item counts for the "Page N" label, and a one-page-ahead prefetch (SPEC 4.1 N2).
/// Not thread-safe; drive it from one logical caller (the tab's view model).
/// </summary>
public sealed class SearchSession
{
    private readonly Func<int, CancellationToken, Task<PostPage>> _fetch;
    private readonly List<int> _pageEnds = [];
    private readonly List<int> _pageNumbers = [];
    private Task<PostPage>? _prefetch;
    private int _prefetchPage = -1;
    private CancellationTokenSource? _prefetchCts;
    private int _itemCount;

    public SearchSession(BooruClient client, ServerContext ctx, PreparedQuery query, int startPage)
        : this((page, ct) => client.FetchPostsAsync(ctx, query, page, ct), startPage)
    {
        Context = ctx;
        Query = query;
    }

    internal SearchSession(Func<int, CancellationToken, Task<PostPage>> fetch, int startPage)
    {
        _fetch = fetch;
        NextPage = startPage;
        StartPage = startPage;
    }

    public ServerContext? Context { get; }
    public PreparedQuery? Query { get; }
    public int StartPage { get; }
    public int NextPage { get; private set; }
    public bool IsLastPage { get; private set; }
    public bool IsLoading { get; private set; }
    public int LoadedPages => _pageEnds.Count;
    public int HiddenTotal { get; private set; }

    /// <summary>Loads the next page, consuming the prefetched one when available. Returns null at the end.</summary>
    public async Task<PostPage?> LoadNextAsync(CancellationToken ct)
    {
        if (IsLastPage || IsLoading) return null;
        IsLoading = true;
        try
        {
            var page = NextPage;
            PostPage result;
            if (_prefetch is not null && _prefetchPage == page)
            {
                var task = _prefetch;
                _prefetch = null;
                try { result = await task.WaitAsync(ct).ConfigureAwait(false); }
                catch (Exception) when (!ct.IsCancellationRequested) { result = await _fetch(page, ct).ConfigureAwait(false); }
            }
            else
            {
                CancelPrefetch();
                result = await _fetch(page, ct).ConfigureAwait(false);
            }

            NextPage = page + 1;
            if (result.RawCount == 0) IsLastPage = true;
            HiddenTotal += result.HiddenCount;
            _itemCount += result.Posts.Count;
            _pageEnds.Add(_itemCount);
            _pageNumbers.Add(page);
            return result;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Starts fetching the next page in the background if nothing is in flight.</summary>
    public void Prefetch()
    {
        if (IsLastPage || IsLoading || _prefetch is not null) return;
        _prefetchCts = new CancellationTokenSource();
        _prefetchPage = NextPage;
        _prefetch = _fetch(NextPage, _prefetchCts.Token);
        _prefetch.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // observe
    }

    public void CancelPrefetch()
    {
        _prefetchCts?.Cancel();
        _prefetchCts = null;
        _prefetch = null;
        _prefetchPage = -1;
    }

    /// <summary>The site page number (as the user thinks of it, 1-based) containing item <paramref name="index"/>.</summary>
    public int DisplayPageOf(int index, int firstPage)
    {
        for (var i = 0; i < _pageEnds.Count; i++)
        {
            if (index < _pageEnds[i]) return _pageNumbers[i] - firstPage + 1;
        }
        return _pageNumbers.Count > 0 ? _pageNumbers[^1] - firstPage + 1 : 1;
    }
}
