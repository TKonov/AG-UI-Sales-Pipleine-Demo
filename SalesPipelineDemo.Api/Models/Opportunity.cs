namespace SalesPipelineDemo.Api.Models;

public class Opportunity
{
    public string Id { get; set; } = "";
    public string Customer { get; set; } = "";
    public decimal Value { get; set; }
    public string? Stage { get; set; }
    public string? Owner { get; set; }
    public double Probability { get; set; }           // 0-100
    public int DaysSinceActivity { get; set; }
    public bool IsApproved { get; set; }
    public bool IsInPreview { get; set; }
    public DateTime CreatedAt { get; set; }

    public Opportunity Clone() => new()
    {
        Id = Id, Customer = Customer, Value = Value, Stage = Stage,
        Owner = Owner, Probability = Probability,
        DaysSinceActivity = DaysSinceActivity,
        IsApproved = IsApproved, IsInPreview = IsInPreview,
        CreatedAt = CreatedAt
    };

    public object? GetFieldValue(string fieldName) => fieldName switch
    {
        "stage"       => Stage,
        "owner"       => Owner,
        "value"       => Value,
        "probability" => Probability,
        _             => null
    };

    public void SetFieldValue(string fieldName, object? value)
    {
        switch (fieldName)
        {
            case "stage":       Stage = value?.ToString(); break;
            case "owner":       Owner = value?.ToString(); break;
            case "value":       if (decimal.TryParse(value?.ToString(), out var d)) Value = d; break;
            case "probability": if (double.TryParse(value?.ToString(), out var p)) Probability = p; break;
        }
    }
}
