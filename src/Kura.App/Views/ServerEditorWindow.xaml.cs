using System.Windows;
using Kura.App.ViewModels;

namespace Kura.App.Views;

public partial class ServerEditorWindow : Window
{
    private bool _loadingKey;

    public ServerEditorWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ServerEditorViewModel old) old.ApiKeyLoaded -= OnApiKeyLoaded;
            if (e.NewValue is ServerEditorViewModel vm)
            {
                vm.ApiKeyLoaded += OnApiKeyLoaded;
                OnApiKeyLoaded(vm.ApiKey);
            }
        };
    }

    // PasswordBox.Password isn't bindable (by design); sync it by hand.
    private void OnApiKeyLoaded(string key)
    {
        _loadingKey = true;
        ApiKeyBox.Password = key;
        _loadingKey = false;
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (!_loadingKey && DataContext is ServerEditorViewModel vm) vm.ApiKey = ApiKeyBox.Password;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
