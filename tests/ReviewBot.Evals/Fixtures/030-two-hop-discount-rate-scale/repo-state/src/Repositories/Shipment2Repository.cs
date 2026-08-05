namespace Scale.Repositories;

using Scale.Domain;

public sealed class Shipment2Repository
{
    private readonly Dictionary<int, Shipment2Record> items = new();

    public Shipment2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Shipment2Record> All() => this.items.Values;

    public void Upsert(Shipment2Record record) => this.items[record.Id] = record;
}
