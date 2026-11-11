using Juniro.App.ViewModels;

namespace Juniro.App.Views;

public partial class StrategiesPage : ContentPage
{
    private readonly StrategiesViewModel _vm;

    public StrategiesPage(StrategiesViewModel vm)
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
