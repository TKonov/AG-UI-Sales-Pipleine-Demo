using Microsoft.AspNetCore.Components;
using SalesPipelineDemo.Client.Models;

namespace SalesPipelineDemo.Client.Components;

public partial class DataGauge
{
    [Parameter] public GaugeDataContract Contract { get; set; } = new();
}
