namespace Scale.Repositories;

using Scale.Domain;

public sealed class Merchant1Repository
{
    private readonly Dictionary<int, Merchant1Record> items = new();

    public Merchant1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Merchant1Record> All() => this.items.Values;

    public void Upsert(Merchant1Record record) => this.items[record.Id] = record;
}
