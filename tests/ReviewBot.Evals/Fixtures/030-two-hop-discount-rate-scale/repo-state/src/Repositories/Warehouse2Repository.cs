namespace Scale.Repositories;

using Scale.Domain;

public sealed class Warehouse2Repository
{
    private readonly Dictionary<int, Warehouse2Record> items = new();

    public Warehouse2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Warehouse2Record> All() => this.items.Values;

    public void Upsert(Warehouse2Record record) => this.items[record.Id] = record;
}
