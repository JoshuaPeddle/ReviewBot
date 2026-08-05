namespace Scale.Repositories;

using Scale.Domain;

public sealed class Catalog3Repository
{
    private readonly Dictionary<int, Catalog3Record> items = new();

    public Catalog3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Catalog3Record> All() => this.items.Values;

    public void Upsert(Catalog3Record record) => this.items[record.Id] = record;
}
