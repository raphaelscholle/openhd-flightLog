using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenHdFlightLog.Models;

public partial class UserVariableRecord : ObservableObject
{
    public long Id { get; set; }

    [ObservableProperty]
    private string name = "";

    [ObservableProperty]
    private string valueText = "";

    [ObservableProperty]
    private string dataType = "text";

    [ObservableProperty]
    private string notes = "";
}
