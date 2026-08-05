namespace Scale.Domain;

public sealed record Subscription1Record(int Id, string Name, decimal Amount, DateTimeOffset UpdatedAt)
{
    public bool IsActive => this.Amount > 0m;
}
