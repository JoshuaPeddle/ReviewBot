namespace Scale.Repositories;

using Scale.Domain;

public sealed class Delivery2Repository
{
    private readonly Dictionary<int, Delivery2Record> items = new();

    public Delivery2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Delivery2Record> All() => this.items.Values;

    public void Upsert(Delivery2Record record) => this.items[record.Id] = record;
}
