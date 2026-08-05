namespace Scale.Repositories;

using Scale.Domain;

public sealed class Supplier1Repository
{
    private readonly Dictionary<int, Supplier1Record> items = new();

    public Supplier1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Supplier1Record> All() => this.items.Values;

    public void Upsert(Supplier1Record record) => this.items[record.Id] = record;
}
