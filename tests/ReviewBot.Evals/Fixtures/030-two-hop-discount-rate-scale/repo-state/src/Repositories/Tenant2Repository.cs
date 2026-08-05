namespace Scale.Repositories;

using Scale.Domain;

public sealed class Tenant2Repository
{
    private readonly Dictionary<int, Tenant2Record> items = new();

    public Tenant2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Tenant2Record> All() => this.items.Values;

    public void Upsert(Tenant2Record record) => this.items[record.Id] = record;
}
