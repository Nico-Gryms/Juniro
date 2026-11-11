using Juniro.App.ViewModels;

namespace Juniro.App.Views;

public partial class LogsPage : ContentPage
{
    private readonly LogsViewModel _vm;

    public LogsPage(LogsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnAppearingAsync();
    }
}
