namespace Scale.Repositories;

using Scale.Domain;

public sealed class Refund2Repository
{
    private readonly Dictionary<int, Refund2Record> items = new();

    public Refund2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Refund2Record> All() => this.items.Values;

    public void Upsert(Refund2Record record) => this.items[record.Id] = record;
}
