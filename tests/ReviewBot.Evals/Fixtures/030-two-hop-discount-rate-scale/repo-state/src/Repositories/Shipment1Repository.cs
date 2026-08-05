namespace Scale.Repositories;

using Scale.Domain;

public sealed class Shipment1Repository
{
    private readonly Dictionary<int, Shipment1Record> items = new();

    public Shipment1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Shipment1Record> All() => this.items.Values;

    public void Upsert(Shipment1Record record) => this.items[record.Id] = record;
}
