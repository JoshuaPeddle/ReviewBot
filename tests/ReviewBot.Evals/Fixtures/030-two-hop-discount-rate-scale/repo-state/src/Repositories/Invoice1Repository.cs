namespace Scale.Repositories;

using Scale.Domain;

public sealed class Invoice1Repository
{
    private readonly Dictionary<int, Invoice1Record> items = new();

    public Invoice1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Invoice1Record> All() => this.items.Values;

    public void Upsert(Invoice1Record record) => this.items[record.Id] = record;
}
