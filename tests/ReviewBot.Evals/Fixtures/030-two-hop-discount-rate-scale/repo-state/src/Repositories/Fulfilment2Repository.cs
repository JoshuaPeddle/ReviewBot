namespace Scale.Repositories;

using Scale.Domain;

public sealed class Fulfilment2Repository
{
    private readonly Dictionary<int, Fulfilment2Record> items = new();

    public Fulfilment2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Fulfilment2Record> All() => this.items.Values;

    public void Upsert(Fulfilment2Record record) => this.items[record.Id] = record;
}
