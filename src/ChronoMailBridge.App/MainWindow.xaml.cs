using System.Windows;
using ChronoMailBridge.App.ViewModels;

namespace ChronoMailBridge.App;

public partial class MainWindow : Window, IAsyncDisposable
{
    private readonly MainWindowViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _viewModel.SetSessionPassword(passwordBox.Password);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _viewModel.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected override async void OnClosed(EventArgs e)
    {
        await DisposeAsync();
        base.OnClosed(e);
    }
}
