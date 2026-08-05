namespace Scale.Repositories;

using Scale.Domain;

public sealed class Tenant1Repository
{
    private readonly Dictionary<int, Tenant1Record> items = new();

    public Tenant1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Tenant1Record> All() => this.items.Values;

    public void Upsert(Tenant1Record record) => this.items[record.Id] = record;
}
