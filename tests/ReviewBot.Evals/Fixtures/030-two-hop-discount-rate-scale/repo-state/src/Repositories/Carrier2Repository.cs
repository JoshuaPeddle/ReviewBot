namespace Scale.Repositories;

using Scale.Domain;

public sealed class Carrier2Repository
{
    private readonly Dictionary<int, Carrier2Record> items = new();

    public Carrier2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Carrier2Record> All() => this.items.Values;

    public void Upsert(Carrier2Record record) => this.items[record.Id] = record;
}
