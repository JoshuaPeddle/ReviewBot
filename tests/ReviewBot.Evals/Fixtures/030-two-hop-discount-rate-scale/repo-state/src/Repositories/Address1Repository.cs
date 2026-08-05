namespace Scale.Repositories;

using Scale.Domain;

public sealed class Address1Repository
{
    private readonly Dictionary<int, Address1Record> items = new();

    public Address1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Address1Record> All() => this.items.Values;

    public void Upsert(Address1Record record) => this.items[record.Id] = record;
}
