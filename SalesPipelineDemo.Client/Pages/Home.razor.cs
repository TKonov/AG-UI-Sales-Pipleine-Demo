using Microsoft.AspNetCore.Components;

namespace SalesPipelineDemo.Client.Pages;

public partial class Home
{
    [Inject] private NavigationManager Nav { get; set; } = default!;

    protected override void OnInitialized() => Nav.NavigateTo("/sales-quality-review");
}
