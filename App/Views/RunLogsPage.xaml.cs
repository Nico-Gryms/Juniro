using Juniro.App.ViewModels;

namespace Juniro.App.Views;

public partial class RunLogsPage : ContentPage
{
    private readonly RunLogsViewModel _vm;

    public RunLogsPage(RunLogsViewModel vm)
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
