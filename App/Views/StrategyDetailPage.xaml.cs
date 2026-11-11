using Juniro.App.ViewModels;

namespace Juniro.App.Views;

public partial class StrategyDetailPage : ContentPage
{
    private readonly StrategyDetailViewModel _vm;

    public StrategyDetailPage(StrategyDetailViewModel vm)
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
