using Juniro.App.ViewModels;

namespace Juniro.App.Views;

public partial class ResultsPage : ContentPage
{
    private readonly ResultsViewModel _vm;

    public ResultsPage(ResultsViewModel vm)
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
