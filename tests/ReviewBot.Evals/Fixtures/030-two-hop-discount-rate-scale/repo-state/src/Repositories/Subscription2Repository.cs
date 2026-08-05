namespace Scale.Repositories;

using Scale.Domain;

public sealed class Subscription2Repository
{
    private readonly Dictionary<int, Subscription2Record> items = new();

    public Subscription2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Subscription2Record> All() => this.items.Values;

    public void Upsert(Subscription2Record record) => this.items[record.Id] = record;
}
