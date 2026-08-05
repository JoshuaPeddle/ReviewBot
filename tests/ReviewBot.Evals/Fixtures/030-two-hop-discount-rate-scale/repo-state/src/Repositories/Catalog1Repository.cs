namespace Scale.Repositories;

using Scale.Domain;

public sealed class Catalog1Repository
{
    private readonly Dictionary<int, Catalog1Record> items = new();

    public Catalog1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Catalog1Record> All() => this.items.Values;

    public void Upsert(Catalog1Record record) => this.items[record.Id] = record;
}
