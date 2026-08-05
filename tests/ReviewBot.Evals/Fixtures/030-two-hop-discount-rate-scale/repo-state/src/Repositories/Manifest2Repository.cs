namespace Scale.Repositories;

using Scale.Domain;

public sealed class Manifest2Repository
{
    private readonly Dictionary<int, Manifest2Record> items = new();

    public Manifest2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Manifest2Record> All() => this.items.Values;

    public void Upsert(Manifest2Record record) => this.items[record.Id] = record;
}
