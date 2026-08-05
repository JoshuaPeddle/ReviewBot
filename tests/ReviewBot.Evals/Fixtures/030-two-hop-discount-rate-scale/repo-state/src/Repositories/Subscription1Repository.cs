namespace Scale.Repositories;

using Scale.Domain;

public sealed class Subscription1Repository
{
    private readonly Dictionary<int, Subscription1Record> items = new();

    public Subscription1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Subscription1Record> All() => this.items.Values;

    public void Upsert(Subscription1Record record) => this.items[record.Id] = record;
}
