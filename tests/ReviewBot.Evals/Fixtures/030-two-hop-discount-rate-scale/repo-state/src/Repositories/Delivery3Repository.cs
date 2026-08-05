namespace Scale.Repositories;

using Scale.Domain;

public sealed class Delivery3Repository
{
    private readonly Dictionary<int, Delivery3Record> items = new();

    public Delivery3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Delivery3Record> All() => this.items.Values;

    public void Upsert(Delivery3Record record) => this.items[record.Id] = record;
}
