namespace Scale.Repositories;

using Scale.Domain;

public sealed class Carrier3Repository
{
    private readonly Dictionary<int, Carrier3Record> items = new();

    public Carrier3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Carrier3Record> All() => this.items.Values;

    public void Upsert(Carrier3Record record) => this.items[record.Id] = record;
}
