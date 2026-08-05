namespace Scale.Repositories;

using Scale.Domain;

public sealed class Reservation2Repository
{
    private readonly Dictionary<int, Reservation2Record> items = new();

    public Reservation2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Reservation2Record> All() => this.items.Values;

    public void Upsert(Reservation2Record record) => this.items[record.Id] = record;
}
