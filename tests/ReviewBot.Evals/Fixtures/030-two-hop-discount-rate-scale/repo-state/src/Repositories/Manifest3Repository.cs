namespace Scale.Repositories;

using Scale.Domain;

public sealed class Manifest3Repository
{
    private readonly Dictionary<int, Manifest3Record> items = new();

    public Manifest3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Manifest3Record> All() => this.items.Values;

    public void Upsert(Manifest3Record record) => this.items[record.Id] = record;
}
