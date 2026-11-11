using Juniro.App.ViewModels;

namespace Juniro.App.Views;

public partial class BetEditPage : ContentPage
{
    private readonly BetEditViewModel _vm;

    public BetEditPage(BetEditViewModel vm)
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
