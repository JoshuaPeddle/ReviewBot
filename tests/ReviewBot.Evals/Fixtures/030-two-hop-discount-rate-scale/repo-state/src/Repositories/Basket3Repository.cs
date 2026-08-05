namespace Scale.Repositories;

using Scale.Domain;

public sealed class Basket3Repository
{
    private readonly Dictionary<int, Basket3Record> items = new();

    public Basket3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Basket3Record> All() => this.items.Values;

    public void Upsert(Basket3Record record) => this.items[record.Id] = record;
}
