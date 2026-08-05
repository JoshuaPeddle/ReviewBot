namespace Scale.Repositories;

using Scale.Domain;

public sealed class Tax1Repository
{
    private readonly Dictionary<int, Tax1Record> items = new();

    public Tax1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Tax1Record> All() => this.items.Values;

    public void Upsert(Tax1Record record) => this.items[record.Id] = record;
}
