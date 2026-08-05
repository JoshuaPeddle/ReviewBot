namespace Scale.Repositories;

using Scale.Domain;

public sealed class Pricing2Repository
{
    private readonly Dictionary<int, Pricing2Record> items = new();

    public Pricing2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Pricing2Record> All() => this.items.Values;

    public void Upsert(Pricing2Record record) => this.items[record.Id] = record;
}
