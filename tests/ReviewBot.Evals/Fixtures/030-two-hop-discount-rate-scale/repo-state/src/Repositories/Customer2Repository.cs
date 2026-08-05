namespace Scale.Repositories;

using Scale.Domain;

public sealed class Customer2Repository
{
    private readonly Dictionary<int, Customer2Record> items = new();

    public Customer2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Customer2Record> All() => this.items.Values;

    public void Upsert(Customer2Record record) => this.items[record.Id] = record;
}
