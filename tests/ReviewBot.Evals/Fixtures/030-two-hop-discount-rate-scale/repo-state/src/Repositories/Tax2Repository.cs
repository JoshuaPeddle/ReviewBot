namespace Scale.Repositories;

using Scale.Domain;

public sealed class Tax2Repository
{
    private readonly Dictionary<int, Tax2Record> items = new();

    public Tax2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Tax2Record> All() => this.items.Values;

    public void Upsert(Tax2Record record) => this.items[record.Id] = record;
}
