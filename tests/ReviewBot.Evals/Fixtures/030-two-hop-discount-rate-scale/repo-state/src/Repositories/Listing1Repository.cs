namespace Scale.Repositories;

using Scale.Domain;

public sealed class Listing1Repository
{
    private readonly Dictionary<int, Listing1Record> items = new();

    public Listing1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Listing1Record> All() => this.items.Values;

    public void Upsert(Listing1Record record) => this.items[record.Id] = record;
}
