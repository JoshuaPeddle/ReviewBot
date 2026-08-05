namespace Scale.Repositories;

using Scale.Domain;

public sealed class Merchant3Repository
{
    private readonly Dictionary<int, Merchant3Record> items = new();

    public Merchant3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Merchant3Record> All() => this.items.Values;

    public void Upsert(Merchant3Record record) => this.items[record.Id] = record;
}
