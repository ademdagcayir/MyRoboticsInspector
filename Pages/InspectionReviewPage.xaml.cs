using MyRoboticsInspector.ViewModels;

namespace MyRoboticsInspector.Pages;

public partial class InspectionReviewPage : ContentPage
{
    private readonly InspectionReviewViewModel _vm;

    public InspectionReviewPage(InspectionReviewViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.Cleanup();
    }
}
