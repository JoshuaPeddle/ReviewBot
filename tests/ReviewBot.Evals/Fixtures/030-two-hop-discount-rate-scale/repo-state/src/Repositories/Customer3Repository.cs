namespace Scale.Repositories;

using Scale.Domain;

public sealed class Customer3Repository
{
    private readonly Dictionary<int, Customer3Record> items = new();

    public Customer3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Customer3Record> All() => this.items.Values;

    public void Upsert(Customer3Record record) => this.items[record.Id] = record;
}
