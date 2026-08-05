namespace Scale.Repositories;

using Scale.Domain;

public sealed class Tariff1Repository
{
    private readonly Dictionary<int, Tariff1Record> items = new();

    public Tariff1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Tariff1Record> All() => this.items.Values;

    public void Upsert(Tariff1Record record) => this.items[record.Id] = record;
}
