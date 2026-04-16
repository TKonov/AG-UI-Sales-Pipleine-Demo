using Microsoft.AspNetCore.Components;
using SalesPipelineDemo.Client.Models;

namespace SalesPipelineDemo.Client.Components;

public partial class DataPanel
{
    [Parameter] public PanelDataContract Contract { get; set; } = new();
}
