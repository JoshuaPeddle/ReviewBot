namespace Scale.Repositories;

using Scale.Domain;

public sealed class Notification1Repository
{
    private readonly Dictionary<int, Notification1Record> items = new();

    public Notification1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Notification1Record> All() => this.items.Values;

    public void Upsert(Notification1Record record) => this.items[record.Id] = record;
}
