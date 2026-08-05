namespace Scale.Repositories;

using Scale.Domain;

public sealed class Listing3Repository
{
    private readonly Dictionary<int, Listing3Record> items = new();

    public Listing3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Listing3Record> All() => this.items.Values;

    public void Upsert(Listing3Record record) => this.items[record.Id] = record;
}
