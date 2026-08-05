namespace Scale.Repositories;

using Scale.Domain;

public sealed class Address2Repository
{
    private readonly Dictionary<int, Address2Record> items = new();

    public Address2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Address2Record> All() => this.items.Values;

    public void Upsert(Address2Record record) => this.items[record.Id] = record;
}
