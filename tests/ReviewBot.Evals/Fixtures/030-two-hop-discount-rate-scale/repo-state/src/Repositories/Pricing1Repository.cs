namespace Scale.Repositories;

using Scale.Domain;

public sealed class Pricing1Repository
{
    private readonly Dictionary<int, Pricing1Record> items = new();

    public Pricing1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Pricing1Record> All() => this.items.Values;

    public void Upsert(Pricing1Record record) => this.items[record.Id] = record;
}
