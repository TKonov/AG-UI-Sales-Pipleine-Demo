using Microsoft.AspNetCore.Components;
using SalesPipelineDemo.Client.Models;

namespace SalesPipelineDemo.Client.Components;

public partial class DataChart
{
    [Parameter] public ChartDataContract Contract { get; set; } = new();

    // Kendo template strings stored in code to avoid Razor preprocessor-directive parsing issues
    private const string seriesLabelTemplate = "#= dataItem.DisplayValue #";
    private const string valueAxisTemplate   = "#= value.toFixed(1) #M";
}
