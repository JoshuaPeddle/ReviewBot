namespace Scale.Repositories;

using Scale.Domain;

public sealed class Tariff2Repository
{
    private readonly Dictionary<int, Tariff2Record> items = new();

    public Tariff2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Tariff2Record> All() => this.items.Values;

    public void Upsert(Tariff2Record record) => this.items[record.Id] = record;
}
