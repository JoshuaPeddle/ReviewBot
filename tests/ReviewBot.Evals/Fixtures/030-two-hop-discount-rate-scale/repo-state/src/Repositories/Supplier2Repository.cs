namespace Scale.Repositories;

using Scale.Domain;

public sealed class Supplier2Repository
{
    private readonly Dictionary<int, Supplier2Record> items = new();

    public Supplier2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Supplier2Record> All() => this.items.Values;

    public void Upsert(Supplier2Record record) => this.items[record.Id] = record;
}
