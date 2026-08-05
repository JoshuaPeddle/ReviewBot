namespace Scale.Repositories;

using Scale.Domain;

public sealed class Notification2Repository
{
    private readonly Dictionary<int, Notification2Record> items = new();

    public Notification2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Notification2Record> All() => this.items.Values;

    public void Upsert(Notification2Record record) => this.items[record.Id] = record;
}
