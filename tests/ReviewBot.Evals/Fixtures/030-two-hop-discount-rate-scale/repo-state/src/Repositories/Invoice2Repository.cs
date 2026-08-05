namespace Scale.Repositories;

using Scale.Domain;

public sealed class Invoice2Repository
{
    private readonly Dictionary<int, Invoice2Record> items = new();

    public Invoice2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Invoice2Record> All() => this.items.Values;

    public void Upsert(Invoice2Record record) => this.items[record.Id] = record;
}
