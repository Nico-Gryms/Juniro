using Juniro.App.ViewModels;

namespace Juniro.App.Views;

public partial class BetsPage : ContentPage
{
    private readonly BetsViewModel _vm;

    public BetsPage(BetsViewModel vm)
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
