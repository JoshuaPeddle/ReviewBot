namespace Scale.Repositories;

using Scale.Domain;

public sealed class Manifest1Repository
{
    private readonly Dictionary<int, Manifest1Record> items = new();

    public Manifest1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Manifest1Record> All() => this.items.Values;

    public void Upsert(Manifest1Record record) => this.items[record.Id] = record;
}
