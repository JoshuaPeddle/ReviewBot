namespace Scale.Repositories;

using Scale.Domain;

public sealed class Refund1Repository
{
    private readonly Dictionary<int, Refund1Record> items = new();

    public Refund1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Refund1Record> All() => this.items.Values;

    public void Upsert(Refund1Record record) => this.items[record.Id] = record;
}
