namespace Scale.Repositories;

using Scale.Domain;

public sealed class Payment1Repository
{
    private readonly Dictionary<int, Payment1Record> items = new();

    public Payment1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Payment1Record> All() => this.items.Values;

    public void Upsert(Payment1Record record) => this.items[record.Id] = record;
}
