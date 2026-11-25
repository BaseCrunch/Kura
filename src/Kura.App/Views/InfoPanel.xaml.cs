using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kura.App.Services;
using Kura.App.ViewModels;
using Kura.Core.Models;

namespace Kura.App.Views;

/// <summary>Permanently docked tag/info panel (SPEC 4.6 U3) for the active tab's focused post.</summary>
public partial class InfoPanel : UserControl
{
    public InfoPanel() => InitializeComponent();

    private SearchTabViewModel? Tab => DataContext as SearchTabViewModel;
    private PostItemViewModel? Post => Tab?.FocusedPost;

    private static Tag? TagOf(object sender) => (sender as FrameworkElement)?.DataContext as Tag;

    private void OnTagClick(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not { } tag) return;
        var action = Keyboard.Modifiers switch
        {
            ModifierKeys.Control => TagAction.Add,
            ModifierKeys.Alt => TagAction.Exclude,
            ModifierKeys.Shift => TagAction.NewTab,
            _ => TagAction.Search,
        };
        Tab?.ApplyTag(tag, action);
    }

    private void OnTagMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && TagOf(sender) is { } tag)
        {
            Tab?.ApplyTag(tag, TagAction.NewTab);
            e.Handled = true;
        }
    }

    private void OnTagMenuSearch(object sender, RoutedEventArgs e) => Menu(sender, TagAction.Search);
    private void OnTagMenuAdd(object sender, RoutedEventArgs e) => Menu(sender, TagAction.Add);
    private void OnTagMenuExclude(object sender, RoutedEventArgs e) => Menu(sender, TagAction.Exclude);
    private void OnTagMenuNewTab(object sender, RoutedEventArgs e) => Menu(sender, TagAction.NewTab);
    private void OnTagMenuCopy(object sender, RoutedEventArgs e) => Shell.Copy(TagOf(sender)?.Name);

    private void Menu(object sender, TagAction action)
    {
        if (TagOf(sender) is { } tag) Tab?.ApplyTag(tag, action);
    }

    private void OnSourceClick(object sender, RoutedEventArgs e) => Shell.OpenUrl((sender as FrameworkElement)?.DataContext as string);
    private void OnOpenPostPage(object sender, RoutedEventArgs e) => Shell.OpenUrl(Post?.Post.PostUrl);
    private void OnCopyFileUrl(object sender, RoutedEventArgs e) => Shell.Copy(Post?.Post.File.Url);
    private void OnCopyPostUrl(object sender, RoutedEventArgs e) => Shell.Copy(Post?.Post.PostUrl);
}
