namespace Scale.Repositories;

using Scale.Domain;

public sealed class Customer1Repository
{
    private readonly Dictionary<int, Customer1Record> items = new();

    public Customer1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Customer1Record> All() => this.items.Values;

    public void Upsert(Customer1Record record) => this.items[record.Id] = record;
}
