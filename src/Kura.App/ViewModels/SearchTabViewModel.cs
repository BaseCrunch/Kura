using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kura.App.Services;
using Kura.Core.Engines;
using Kura.Core.Models;
using Kura.Core.Net;
using Kura.Core.Search;

namespace Kura.App.ViewModels;

public enum TagAction { Search, Add, Exclude, NewTab }

/// <summary>One browsing tab: query, server, paged results, autocomplete and the viewer for its results.</summary>
public sealed partial class SearchTabViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private SearchSession? _session;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _suggestCts;
    private int _firstPage;

    public SearchTabViewModel(AppServices services, MainViewModel main, Server server, string query)
    {
        _services = services;
        _main = main;
        _selectedServer = server;
        _queryText = query;
        _title = TitleFor(server, query);
        Viewer = new ViewerViewModel(services, this);
    }

    public MainViewModel Main => _main;
    public ObservableCollection<PostItemViewModel> Posts { get; } = [];
    public ObservableCollection<TagSuggestion> Suggestions { get; } = [];
    public ViewerViewModel Viewer { get; }

    [ObservableProperty] private Server _selectedServer;
    [ObservableProperty] private string _queryText;
    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _serverMessage;
    [ObservableProperty] private bool _isEndReached;
    [ObservableProperty] private bool _hasSearched;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _pageLabel = "";
    [ObservableProperty] private PostItemViewModel? _focusedPost;
    [ObservableProperty] private bool _isSuggestOpen;
    [ObservableProperty] private int _selectedSuggestionIndex = -1;

    public bool IsEmpty => HasSearched && !IsLoading && Posts.Count == 0 && ErrorMessage is null;
    public int HiddenCount => _session?.HiddenTotal ?? 0;
    public ServerContext? Context => _session?.Context;

    /// <summary>width:/height: searches show originals in the viewer (SPEC 1.4 step 5).</summary>
    public bool QueryForcesOriginal => _session?.Query?.ForceOriginal == true;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnHasSearchedChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnIsSelectedChanged(bool value) => Viewer.OnTabActiveChanged(value);

    private bool _suppressServerSearch;

    /// <summary>Swaps in a refreshed instance of the same server without re-running the search.</summary>
    public void ReplaceServer(Server server)
    {
        _suppressServerSearch = true;
        try { SelectedServer = server; }
        finally { _suppressServerSearch = false; }
    }

    partial void OnSelectedServerChanged(Server value)
    {
        if (value is null || _suppressServerSearch) return;
        _services.Settings.Set(SettingKeys.LastServerId, value.Id);
        if (HasSearched) _ = SearchAsync();
    }

    private static string TitleFor(Server server, string query) =>
        string.IsNullOrWhiteSpace(query) ? server.Name : query.Trim();

    [RelayCommand]
    public async Task SearchAsync()
    {
        IsSuggestOpen = false;
        _searchCts?.Cancel();
        _session?.CancelPrefetch();
        var cts = _searchCts = new CancellationTokenSource();

        Viewer.Close();
        Posts.Clear();
        FocusedPost = null;
        ErrorMessage = null;
        ServerMessage = null;
        IsEndReached = false;
        HasSearched = true;
        PageLabel = "";
        Title = TitleFor(SelectedServer, QueryText);

        try
        {
            var server = SelectedServer;
            var creds = _services.Credentials.Get(server.Id);
            var ctx = _services.Client.CreateContext(server, creds);
            var engine = _services.Client.Engines.Get(server.Engine);
            var prepared = _services.Client.Prepare(ctx, QueryText);
            _firstPage = engine.FirstPage;
            _session = new SearchSession(_services.Client, ctx, prepared, engine.FirstPage);
            var q = QueryText.Trim();
            if (q.Length > 0) _ = Task.Run(() => _services.History.Record(q, server.Id), CancellationToken.None);
        }
        catch (NotSupportedException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        await LoadMoreAsync(cts.Token);
    }

    [RelayCommand]
    public Task LoadMoreAsync() => LoadMoreAsync(_searchCts?.Token ?? CancellationToken.None);

    private async Task LoadMoreAsync(CancellationToken ct)
    {
        var session = _session;
        if (session is null || session.IsLoading || session.IsLastPage || ErrorMessage is not null) return;
        IsLoading = true;
        try
        {
            // A page the blacklist/visibility rules emptied completely produces no new rows, so the grid would
            // never ask again: keep going (bounded) until something is visible.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var page = await session.LoadNextAsync(ct);
                if (ct.IsCancellationRequested || session != _session) return;
                if (page is null) break;
                if (page.ServerMessage is not null) ServerMessage = page.ServerMessage;
                await AppendAsync(page, session, ct);
                if (page.Posts.Count > 0 || session.IsLastPage) break;
            }
            IsEndReached = session.IsLastPage;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (BooruRequestException ex)
        {
            if (session == _session) ErrorMessage = ex.Message;
        }
        finally
        {
            if (session == _session)
            {
                IsLoading = false;
                UpdateStatus();
            }
        }
    }

    private async Task AppendAsync(PostPage page, SearchSession session, CancellationToken ct)
    {
        if (page.Posts.Count == 0) return;
        var tagCache = _services.TagCache;
        // Colour uncategorised tags (Gelbooru, e621 v2) from the local cache; feed it from categorised engines.
        var resolved = await Task.Run(() =>
        {
            var unknown = page.Posts.SelectMany(p => p.Tags).Where(t => t.Category == TagCategory.Unknown).Select(t => t.Name);
            var map = tagCache.Lookup(unknown);
            var categorised = page.Posts.SelectMany(p => p.Tags).Where(t => t.Category is not TagCategory.Unknown);
            tagCache.Upsert(categorised);
            return page.Posts.Select(p => (IReadOnlyList<Tag>)p.Tags
                .Select(t => t.Category == TagCategory.Unknown && map.TryGetValue(t.Name, out var c) ? t with { Category = c } : t)
                .ToList()).ToList();
        }, ct);
        if (ct.IsCancellationRequested || session != _session) return;

        var start = Posts.Count;
        var pageNo = session.DisplayPageOf(start, _firstPage);
        var serverName = SelectedServer.Name;
        for (var i = 0; i < page.Posts.Count; i++)
            Posts.Add(new PostItemViewModel(page.Posts[i], start + i, pageNo, serverName, resolved[i]) { IsFavorite = page.Posts[i].IsFavorite });
        FocusedPost ??= Posts.FirstOrDefault();
    }

    [RelayCommand]
    private Task RetryAsync()
    {
        if (_session is null || Posts.Count == 0) return SearchAsync();
        ErrorMessage = null;
        return LoadMoreAsync();
    }

    private void UpdateStatus()
    {
        if (!HasSearched) { StatusText = ""; return; }
        var parts = new List<string> { $"{Posts.Count:N0} posts" };
        if (_session is { LoadedPages: > 0 } s) parts.Add($"{s.LoadedPages} page{(s.LoadedPages == 1 ? "" : "s")} loaded");
        if (HiddenCount > 0) parts.Add($"{HiddenCount:N0} unavailable");
        if (IsEndReached && Posts.Count > 0) parts.Add("end of results");
        StatusText = string.Join("  ·  ", parts);
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Called by the grid as it scrolls. Loads within 1.5 viewports of the end; prefetches within 4.</summary>
    public void OnScrolled(double remainingPx, double viewportPx, int firstVisibleIndex)
    {
        if (firstVisibleIndex >= 0 && firstVisibleIndex < Posts.Count) PageLabel = $"Page {Posts[firstVisibleIndex].PageNumber}";
        if (_session is null || ErrorMessage is not null) return;
        if (remainingPx < viewportPx * 1.5) _ = LoadMoreAsync();
        else if (remainingPx < viewportPx * 4) _session.Prefetch();
    }

    public void ApplyTag(Tag tag, TagAction action)
    {
        var name = tag.Name;
        switch (action)
        {
            case TagAction.Search:
                QueryText = name;
                break;
            case TagAction.Add:
                QueryText = AppendToken(QueryText, name);
                break;
            case TagAction.Exclude:
                QueryText = AppendToken(QueryText, "-" + name);
                break;
            case TagAction.NewTab:
                _main.NewTab(name, SelectedServer);
                return;
        }
        _ = SearchAsync();
    }

    /// <summary>The current server engine's term separator (" " or ", ").</summary>
    private string Separator =>
        _services.Client.Engines.IsSupported(SelectedServer.Engine) ? _services.Client.Engines.Get(SelectedServer.Engine).TagSeparator : " ";

    private string AppendToken(string query, string token)
    {
        var sep = Separator;
        var tokens = QueryPreprocessor.Tokenize(query, sep).ToList();
        var bare = token.TrimStart('-');
        tokens.RemoveAll(t => t.TrimStart('-').Equals(bare, StringComparison.OrdinalIgnoreCase));
        tokens.Add(token);
        return string.Join(sep, tokens);
    }

    public void OpenViewer(int index) => Viewer.Open(index);

    // ----- Autocomplete -----

    /// <summary>Debounced (150 ms) suggestions for the token under the caret.</summary>
    public async void UpdateSuggestions(string text, int caret)
    {
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        var (token, _, _) = AutocompleteService.CurrentToken(text, caret, Separator);
        var (_, term) = AutocompleteService.SplitOperator(token);
        if (term.Length < 1)
        {
            IsSuggestOpen = false;
            return;
        }
        try
        {
            await Task.Delay(150, cts.Token);
            var server = SelectedServer;
            var ctx = _services.Client.CreateContext(server, _services.Credentials.Get(server.Id));
            var local = _services.Autocomplete.Local(term);
            if (cts.IsCancellationRequested) return;
            ShowSuggestions(local);
            var all = await _services.Autocomplete.SuggestAsync(ctx, term, cts.Token);
            if (cts.IsCancellationRequested) return;
            ShowSuggestions(all);
        }
        catch (OperationCanceledException) { }
        catch (NotSupportedException) { }
    }

    private void ShowSuggestions(IReadOnlyList<TagSuggestion> items)
    {
        Suggestions.Clear();
        foreach (var s in items) Suggestions.Add(s);
        SelectedSuggestionIndex = -1;
        IsSuggestOpen = Suggestions.Count > 0;
    }

    public void CloseSuggestions()
    {
        _suggestCts?.Cancel();
        IsSuggestOpen = false;
    }

    /// <summary>Replaces the token under the caret with the suggestion, keeping any -/~ operator. Returns the new caret.</summary>
    public int ApplySuggestion(TagSuggestion s, int caret)
    {
        var text = QueryText;
        var sep = Separator;
        var (token, start, length) = AutocompleteService.CurrentToken(text, caret, sep);
        var (op, _) = AutocompleteService.SplitOperator(token);
        var insert = (s.FromHistory ? s.Name : op + s.Name) + sep;
        var rest = text[(start + length)..].TrimStart(' ', ',');
        QueryText = text[..start] + insert + rest;
        CloseSuggestions();
        return start + insert.Length;
    }
}
