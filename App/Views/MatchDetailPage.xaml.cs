using Juniro.App.ViewModels;

namespace Juniro.App.Views;

public partial class MatchDetailPage : ContentPage
{
    private readonly MatchDetailViewModel _vm;

    public MatchDetailPage(MatchDetailViewModel vm)
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
