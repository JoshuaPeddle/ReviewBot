namespace Scale.Repositories;

using Scale.Domain;

public sealed class Catalog2Repository
{
    private readonly Dictionary<int, Catalog2Record> items = new();

    public Catalog2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Catalog2Record> All() => this.items.Values;

    public void Upsert(Catalog2Record record) => this.items[record.Id] = record;
}
