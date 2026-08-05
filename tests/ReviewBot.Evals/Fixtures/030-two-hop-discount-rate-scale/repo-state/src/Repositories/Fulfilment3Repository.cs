namespace Scale.Repositories;

using Scale.Domain;

public sealed class Fulfilment3Repository
{
    private readonly Dictionary<int, Fulfilment3Record> items = new();

    public Fulfilment3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Fulfilment3Record> All() => this.items.Values;

    public void Upsert(Fulfilment3Record record) => this.items[record.Id] = record;
}
