using Juniro.App.ViewModels;

namespace Juniro.App.Views;

public partial class MorePage : ContentPage
{
    private readonly MoreViewModel _vm;

    public MorePage(MoreViewModel vm)
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
