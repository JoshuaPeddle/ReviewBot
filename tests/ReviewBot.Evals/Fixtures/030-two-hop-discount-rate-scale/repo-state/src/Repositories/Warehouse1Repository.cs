namespace Scale.Repositories;

using Scale.Domain;

public sealed class Warehouse1Repository
{
    private readonly Dictionary<int, Warehouse1Record> items = new();

    public Warehouse1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Warehouse1Record> All() => this.items.Values;

    public void Upsert(Warehouse1Record record) => this.items[record.Id] = record;
}
