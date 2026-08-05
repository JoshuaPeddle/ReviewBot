namespace Scale.Repositories;

using Scale.Domain;

public sealed class Invoice3Repository
{
    private readonly Dictionary<int, Invoice3Record> items = new();

    public Invoice3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Invoice3Record> All() => this.items.Values;

    public void Upsert(Invoice3Record record) => this.items[record.Id] = record;
}
