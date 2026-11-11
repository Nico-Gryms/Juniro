using Juniro.App.ViewModels;

namespace Juniro.App.Views;

public partial class StandingsPage : ContentPage
{
    private readonly StandingsViewModel _vm;

    public StandingsPage(StandingsViewModel vm)
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
