using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kura.App.Services;
using Kura.App.ViewModels;
using Kura.Core.Models;

namespace Kura.App.Views;

public partial class SearchTabView : UserControl
{
    private static readonly ConditionalWeakTable<SearchTabViewModel, SearchTabView> Views = new();
    private bool _applyingSuggestion;
    private IDisposable? _popupToken;
    private IDisposable? _dropDownToken;

    public SearchTabView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is SearchTabViewModel vm) Views.AddOrUpdate(vm, this);
        };
        Unloaded += (_, _) =>
        {
            _popupToken?.Dispose();
            _dropDownToken?.Dispose();
        };
    }

    private SearchTabViewModel Vm => (SearchTabViewModel)DataContext;

    public static void FocusSearchOf(SearchTabViewModel? tab)
    {
        if (tab is null || !Views.TryGetValue(tab, out var view)) return;
        if (tab.Viewer.IsOpen) tab.Viewer.Close();
        view.SearchBox.Focus();
        view.SearchBox.SelectAll();
    }

    // ----- Autocomplete wiring -----

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_applyingSuggestion || !SearchBox.IsKeyboardFocused) return;
        Vm.UpdateSuggestions(SearchBox.Text, SearchBox.CaretIndex);
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        var open = vm.IsSuggestOpen && vm.Suggestions.Count > 0;
        switch (e.Key)
        {
            case Key.Down when open:
                vm.SelectedSuggestionIndex = Math.Min(vm.Suggestions.Count - 1, vm.SelectedSuggestionIndex + 1);
                SuggestList.ScrollIntoView(SuggestList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when open:
                vm.SelectedSuggestionIndex = Math.Max(-1, vm.SelectedSuggestionIndex - 1);
                if (SuggestList.SelectedItem is not null) SuggestList.ScrollIntoView(SuggestList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Tab when open:
                Apply(vm.Suggestions[Math.Max(0, vm.SelectedSuggestionIndex)]);
                e.Handled = true;
                break;
            case Key.Enter when open && vm.SelectedSuggestionIndex >= 0:
                Apply(vm.Suggestions[vm.SelectedSuggestionIndex]);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.CloseSuggestions();
                _ = vm.SearchAsync();
                Keyboard.ClearFocus();
                FocusGrid();
                e.Handled = true;
                break;
            case Key.Escape when open:
                vm.CloseSuggestions();
                e.Handled = true;
                break;
            case Key.Escape:
                FocusGrid();
                e.Handled = true;
                break;
        }
    }

    private void Apply(TagSuggestion s)
    {
        _applyingSuggestion = true;
        try
        {
            var caret = Vm.ApplySuggestion(s, SearchBox.CaretIndex);
            SearchBox.CaretIndex = Math.Min(caret, SearchBox.Text.Length);
        }
        finally
        {
            _applyingSuggestion = false;
        }
    }

    private void OnSuggestionClicked(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TagSuggestion s)
        {
            Apply(s);
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void OnSearchLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Delay so a click on a suggestion lands before the popup closes.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!SearchBox.IsKeyboardFocused && !SuggestPopup.IsMouseOver) Vm.CloseSuggestions();
        });
    }

    private void FocusGrid() => ResultsGrid.Focus();

    // ----- Overlay gate (video airspace) -----

    private void OnPopupOpened(object? sender, EventArgs e) => _popupToken ??= AppServices.Current.Overlay.Enter();

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        _popupToken?.Dispose();
        _popupToken = null;
    }

    private void OnDropDownOpened(object? sender, EventArgs e) => _dropDownToken ??= AppServices.Current.Overlay.Enter();

    private void OnDropDownClosed(object? sender, EventArgs e)
    {
        _dropDownToken?.Dispose();
        _dropDownToken = null;
    }
}
