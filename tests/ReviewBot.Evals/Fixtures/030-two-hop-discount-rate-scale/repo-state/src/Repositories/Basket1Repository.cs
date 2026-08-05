namespace Scale.Repositories;

using Scale.Domain;

public sealed class Basket1Repository
{
    private readonly Dictionary<int, Basket1Record> items = new();

    public Basket1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Basket1Record> All() => this.items.Values;

    public void Upsert(Basket1Record record) => this.items[record.Id] = record;
}
