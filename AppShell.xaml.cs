using MyRoboticsInspector.Pages;

namespace MyRoboticsInspector;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("inspectiondetail", typeof(InspectionDetailPage));
        Routing.RegisterRoute("inspectionreview", typeof(InspectionReviewPage));
        Routing.RegisterRoute("projectform", typeof(ProjectFormPage));
        Routing.RegisterRoute("projectchannels", typeof(ProjectChannelsPage));
        Routing.RegisterRoute("channeledit", typeof(ChannelEditPage));
    }

    private void OnExitClicked(object sender, EventArgs e)
    {
        Application.Current?.Quit();
    }

}
