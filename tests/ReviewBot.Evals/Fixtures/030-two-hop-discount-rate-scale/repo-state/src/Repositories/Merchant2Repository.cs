namespace Scale.Repositories;

using Scale.Domain;

public sealed class Merchant2Repository
{
    private readonly Dictionary<int, Merchant2Record> items = new();

    public Merchant2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Merchant2Record> All() => this.items.Values;

    public void Upsert(Merchant2Record record) => this.items[record.Id] = record;
}
