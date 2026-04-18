using SalesPipelineDemo.Api.Models;

namespace SalesPipelineDemo.Api.Services;

/// <summary>
/// In-memory store with 30 hardcoded opportunities (mimics the 150-opp demo
/// but kept small enough to be readable in a demo).  Starts with realistic
/// data quality issues: missing stages, stale deals, no owners, low probability.
/// </summary>
public class OpportunityStore
{
    private readonly List<Opportunity> _opportunities;
    private readonly decimal _startingForecast;

    public OpportunityStore()
    {
        _opportunities = BuildSeedData();
        _startingForecast = _opportunities.Sum(o => o.Value * (decimal)(o.Probability / 100.0));
    }

    /// <summary>
    /// Returns a clone of all opportunities in the store.
    /// </summary>
    public List<Opportunity> GetAll() =>
        _opportunities.Select(o => o.Clone()).ToList();

    /// <summary>
    /// Returns a clone of a specific opportunity by ID.
    /// </summary>
    public Opportunity? Get(string id) =>
        _opportunities.FirstOrDefault(o => o.Id == id)?.Clone();

    /// <summary>
    /// Updates a field on a specific opportunity and checks for data quality approval.
    /// </summary>
    public void Update(string id, string field, object? value)
    {
        var opp = _opportunities.FirstOrDefault(o => o.Id == id);
        if (opp is null) return;
        
        opp.SetFieldValue(field, value);
        opp.IsInPreview = false;
        
        // Auto-approve only if it's now fully clean
        if (IsClean(opp))
        {
            opp.IsApproved = true;
        }
    }

    private static bool IsClean(Opportunity o) =>
        !string.IsNullOrEmpty(o.Stage) && 
        !string.IsNullOrEmpty(o.Owner) && 
        o.DaysSinceActivity < 60;

    /// <summary>
    /// Finds opportunities matching specific criteria (stale, missing-owner, missing-stage).
    /// </summary>
    public List<Opportunity> FindMatching(string criteria) =>
        criteria switch
        {
            "stale"         => _opportunities.Where(o => o.DaysSinceActivity >= 60).Select(o => o.Clone()).ToList(),
            "missing-owner" => _opportunities.Where(o => string.IsNullOrEmpty(o.Owner)).Select(o => o.Clone()).ToList(),
            "missing-stage" => _opportunities.Where(o => string.IsNullOrEmpty(o.Stage)).Select(o => o.Clone()).ToList(),
            _               => []
        };

    public decimal StartingForecast => _startingForecast;

    private static List<Opportunity> BuildSeedData()
    {
        var stages = new[] { "Prospecting", "Qualified", "Demo", "Proposal", "Negotiation" };
        var owners = new[] { "Alice Johnson", "Bob Martinez", "Carol White", "David Lee", "John Smith" };
        var customers = new[]
        {
            "Acme Corp", "Globex Inc", "Initech Ltd", "Umbrella Co", "Waystar Royco",
            "Pied Piper", "Hooli Corp", "Dunder Mifflin", "Prestige Worldwide", "Vandelay Industries",
            "Sterling Cooper", "Bluth Company", "Wernham Hogg", "Paper Street Soap", "Los Pollos",
            "Sabre Corp", "Wolfram Hart", "Massive Dynamic", "Oceanic Airlines", "Buy More"
        };

        var rng = new Random(42);
        var opps = new List<Opportunity>();

        for (int i = 1; i <= 30; i++)
        {
            var hasStage   = i > 5;          // first 5 are missing stage
            var hasOwner   = i > 3 && i < 28; // first 3 and last 2 are missing owner
            var isStale    = i >= 18 && i <= 22; // 5 stale deals (60+ days)
            var prob       = hasStage ? rng.Next(20, 85) : rng.Next(5, 25);

            opps.Add(new Opportunity
            {
                Id               = $"OPP-{i:D3}",
                Customer         = customers[(i - 1) % customers.Length],
                Value            = rng.Next(20, 800) * 1000m,
                Stage            = hasStage ? stages[rng.Next(stages.Length)] : null,
                Owner            = hasOwner ? owners[rng.Next(owners.Length)] : null,
                Probability      = prob,
                DaysSinceActivity = isStale ? rng.Next(61, 120) : rng.Next(1, 45),
                IsApproved       = false,
                IsInPreview      = false,
                CreatedAt        = DateTime.UtcNow.AddDays(-rng.Next(10, 200))
            });
        }

        return opps;
    }
}
