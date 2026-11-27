using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kura.App.Services;
using Kura.Core.Data;

namespace Kura.App.ViewModels;

public sealed partial class HistoryItemViewModel(SearchHistoryEntry entry, string? serverName) : ObservableObject
{
    public SearchHistoryEntry Entry { get; } = entry;
    public string Query => Entry.Query;
    public string? ServerName { get; } = serverName;

    [ObservableProperty] private bool _isStarred = entry.IsStarred;

    public string Detail
    {
        get
        {
            var when = Entry.When is { } w ? Ago(DateTimeOffset.UtcNow - w) : "";
            return string.Join("  ·  ", new[] { ServerName, when, Entry.UseCount > 1 ? $"{Entry.UseCount}×" : null }
                .Where(s => !string.IsNullOrEmpty(s)));
        }
    }

    private static string Ago(TimeSpan t) => t.TotalMinutes switch
    {
        < 1 => "just now",
        < 60 => $"{(int)t.TotalMinutes} min ago",
        < 60 * 24 => $"{(int)t.TotalHours} h ago",
        < 60 * 24 * 30 => $"{(int)t.TotalDays} d ago",
        _ => $"{(int)(t.TotalDays / 30)} mo ago",
    };
}

/// <summary>Search history flyout (the original's history button and right panel, SPEC 3.1).</summary>
public sealed partial class HistoryViewModel(AppServices services, MainViewModel main) : ObservableObject
{
    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _filter = "";

    public bool IsEmpty => Items.Count == 0;

    partial void OnIsOpenChanged(bool value)
    {
        if (value) Refresh();
    }

    partial void OnFilterChanged(string value) => Refresh();

    public void Refresh()
    {
        Items.Clear();
        var entries = string.IsNullOrWhiteSpace(Filter) ? services.History.Recent(300) : services.History.Find(Filter.Trim(), 300);
        foreach (var e in entries)
            Items.Add(new HistoryItemViewModel(e, main.Servers.FirstOrDefault(s => s.Id == e.ServerId)?.Name));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    /// <summary>Runs a past search on the server it was made on (or the current one).</summary>
    public void Run(HistoryItemViewModel item, bool newTab)
    {
        IsOpen = false;
        var server = main.Servers.FirstOrDefault(s => s.Id == item.Entry.ServerId)
                     ?? main.SelectedTab?.SelectedServer ?? main.Servers.First();
        if (newTab || main.SelectedTab is null) _ = main.OpenSearchAsync(server, item.Query, null);
        else
        {
            var tab = main.SelectedTab;
            tab.ReplaceServer(server);
            tab.QueryText = item.Query;
            _ = tab.SearchAsync();
        }
    }

    [RelayCommand]
    private void ToggleStar(HistoryItemViewModel item)
    {
        item.IsStarred = !item.IsStarred;
        services.History.SetStarred(item.Entry.Id, item.IsStarred);
    }

    [RelayCommand]
    private void Delete(HistoryItemViewModel item)
    {
        services.History.Delete(item.Entry.Id);
        Items.Remove(item);
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void ClearUnstarred()
    {
        services.History.ClearUnstarred();
        Refresh();
    }
}
