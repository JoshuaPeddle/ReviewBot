namespace Scale.Repositories;

using Scale.Domain;

public sealed class Address3Repository
{
    private readonly Dictionary<int, Address3Record> items = new();

    public Address3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Address3Record> All() => this.items.Values;

    public void Upsert(Address3Record record) => this.items[record.Id] = record;
}
