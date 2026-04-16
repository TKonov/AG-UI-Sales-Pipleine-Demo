using Microsoft.AspNetCore.Components;
using SalesPipelineDemo.Client.Models;

namespace SalesPipelineDemo.Client.Components;

public partial class DataGrid
{
    [Parameter] public GridDataContract Contract { get; set; } = new();
    [Parameter] public EventCallback<GridEditEvent> OnCellEdit { get; set; }
    [Parameter] public EventCallback OnSubmit { get; set; }

    private string HeaderGridStyle =>
        $"display:grid;grid-template-columns:{string.Join(" ", Contract.Columns.Select(c => $"{c.Width}px"))};";

    private void StartEdit(GridRow row, ColumnDefinition col)
    {
        if (!row.IsEditable || !col.IsEditable) return;
        foreach (var r in Contract.Rows)
            r.EditingCellField = null;
        row.EditingCellField = col.FieldName;
    }

    private async Task SaveEdit(GridRow row, ColumnDefinition col, object? newValue)
    {
        var oldValue = row.Values.TryGetValue(col.FieldName, out var v) ? v : null;
        row.EditingCellField = null;
        await OnCellEdit.InvokeAsync(new GridEditEvent
        {
            RowId     = row.RowId,
            FieldName = col.FieldName,
            OldValue  = oldValue,
            NewValue  = newValue
        });
    }
}
