namespace Scale.Repositories;

using Scale.Domain;

public sealed class Carrier1Repository
{
    private readonly Dictionary<int, Carrier1Record> items = new();

    public Carrier1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Carrier1Record> All() => this.items.Values;

    public void Upsert(Carrier1Record record) => this.items[record.Id] = record;
}
