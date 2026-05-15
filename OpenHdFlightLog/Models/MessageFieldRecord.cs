using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenHdFlightLog.Models;

public partial class MessageFieldRecord : ObservableObject
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public string MessageName { get; set; } = "";

    [ObservableProperty]
    private string fieldName = "";

    [ObservableProperty]
    private string valueText = "";

    [ObservableProperty]
    private double? numericValue;

    [ObservableProperty]
    private string unit = "";
}
