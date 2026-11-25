using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kura.App.Services;
using Kura.Core.Models;

namespace Kura.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;

    public MainViewModel(AppServices services)
    {
        _services = services;
        _thumbnailHeight = services.Settings.Get(SettingKeys.ThumbnailHeight, 220);
        _infoPanelVisible = services.Settings.Get(SettingKeys.InfoPanelVisible, true);
        _infoPanelWidth = services.Settings.Get(SettingKeys.InfoPanelWidth, 320.0);
        ReloadServers();
        var lastId = services.Settings.Get(SettingKeys.LastServerId, 0L);
        var first = Servers.FirstOrDefault(s => s.Id == lastId) ?? Servers.First();
        var tab = NewTab("", first);
        _ = tab.SearchAsync();
    }

    public AppServices Services => _services;
    public ObservableCollection<SearchTabViewModel> Tabs { get; } = [];
    public ObservableCollection<Server> Servers { get; } = [];

    [ObservableProperty] private SearchTabViewModel? _selectedTab;
    [ObservableProperty] private int _thumbnailHeight;
    [ObservableProperty] private bool _infoPanelVisible;
    [ObservableProperty] private double _infoPanelWidth;

    /// <summary>Raised when the view should open the server editor (the view owns windows).</summary>
    public event Action? ServerEditorRequested;

    partial void OnSelectedTabChanged(SearchTabViewModel? oldValue, SearchTabViewModel? newValue)
    {
        // Deselect first so a playing video in the old tab pauses and detaches before the new tab shows.
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    partial void OnThumbnailHeightChanged(int value) => _services.Settings.Set(SettingKeys.ThumbnailHeight, value);
    partial void OnInfoPanelVisibleChanged(bool value) => _services.Settings.Set(SettingKeys.InfoPanelVisible, value);
    partial void OnInfoPanelWidthChanged(double value) => _services.Settings.Set(SettingKeys.InfoPanelWidth, value);

    public SearchTabViewModel NewTab(string query, Server? server = null, bool select = true)
    {
        var tab = new SearchTabViewModel(_services, this, server ?? SelectedTab?.SelectedServer ?? Servers.First(), query);
        var at = SelectedTab is null ? Tabs.Count : Tabs.IndexOf(SelectedTab) + 1;
        Tabs.Insert(at, tab);
        if (select) SelectedTab = tab;
        if (!string.IsNullOrWhiteSpace(query)) _ = tab.SearchAsync();
        return tab;
    }

    [RelayCommand]
    private void NewEmptyTab()
    {
        var tab = NewTab("");
        _ = tab.SearchAsync();
    }

    [RelayCommand]
    public void CloseTab(SearchTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null) return;
        var index = Tabs.IndexOf(tab);
        tab.Viewer.Close();
        tab.IsSelected = false;
        Tabs.Remove(tab);
        if (Tabs.Count == 0)
        {
            NewEmptyTab();
            return;
        }
        if (SelectedTab == tab || SelectedTab is null) SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
    }

    [RelayCommand]
    private void NextTab() => CycleTab(+1);

    [RelayCommand]
    private void PreviousTab() => CycleTab(-1);

    private void CycleTab(int delta)
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        var i = (Tabs.IndexOf(SelectedTab) + delta + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[i];
    }

    [RelayCommand]
    private void ToggleInfoPanel() => InfoPanelVisible = !InfoPanelVisible;

    [RelayCommand]
    private void OpenServers() => ServerEditorRequested?.Invoke();

    [RelayCommand]
    private void Reload()
    {
        if (SelectedTab is { } t) _ = t.SearchAsync();
    }

    public void ApplyTag(Tag tag, TagAction action) => SelectedTab?.ApplyTag(tag, action);

    /// <summary>Re-reads servers after editing, keeping each tab's selection where the server still exists.</summary>
    public void ReloadServers()
    {
        var all = _services.Servers.GetAll().Where(s => _services.Client.Engines.IsSupported(s.Engine)).ToList();
        Servers.Clear();
        foreach (var s in all) Servers.Add(s);
        foreach (var tab in Tabs)
        {
            var same = Servers.FirstOrDefault(s => s.Id == tab.SelectedServer.Id);
            if (same is not null) tab.ReplaceServer(same);
            else tab.SelectedServer = Servers.First(); // its server was deleted: switch and re-search
        }
    }
}
