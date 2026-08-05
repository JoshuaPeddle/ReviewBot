namespace Scale.Repositories;

using Scale.Domain;

public sealed class Basket2Repository
{
    private readonly Dictionary<int, Basket2Record> items = new();

    public Basket2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Basket2Record> All() => this.items.Values;

    public void Upsert(Basket2Record record) => this.items[record.Id] = record;
}
