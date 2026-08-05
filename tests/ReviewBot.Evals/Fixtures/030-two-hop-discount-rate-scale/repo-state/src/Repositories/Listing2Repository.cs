namespace Scale.Repositories;

using Scale.Domain;

public sealed class Listing2Repository
{
    private readonly Dictionary<int, Listing2Record> items = new();

    public Listing2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Listing2Record> All() => this.items.Values;

    public void Upsert(Listing2Record record) => this.items[record.Id] = record;
}
