using System.ComponentModel;
using AppleDrive.App.Views;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AppleDrive.App;

public sealed partial class MainWindow : Microsoft.UI.Xaml.Window
{
    private readonly ShellViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        Title = Strings.AppTitle;

        AppWindow.Resize(new SizeInt32(1180, 800));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 720;
            presenter.PreferredMinimumHeight = 560;
        }

        _viewModel = App.Services.GetRequiredService<ShellViewModel>();
        _viewModel.PropertyChanged += OnShellPropertyChanged;
        ShowCurrentPage();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.ShowWelcome))
        {
            ShowCurrentPage();
        }
    }

    private void ShowCurrentPage() =>
        RootFrame.Navigate(_viewModel.ShowWelcome ? typeof(WelcomePage) : typeof(ShellPage));
}
