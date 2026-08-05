namespace Scale.Repositories;

using Scale.Domain;

public sealed class Reservation1Repository
{
    private readonly Dictionary<int, Reservation1Record> items = new();

    public Reservation1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Reservation1Record> All() => this.items.Values;

    public void Upsert(Reservation1Record record) => this.items[record.Id] = record;
}
