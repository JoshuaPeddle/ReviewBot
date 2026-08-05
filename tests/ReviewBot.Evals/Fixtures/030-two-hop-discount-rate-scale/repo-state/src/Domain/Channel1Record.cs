namespace Scale.Domain;

public sealed record Channel1Record(int Id, string Name, decimal Amount, DateTimeOffset UpdatedAt)
{
    public bool IsActive => this.Amount > 0m;
}
