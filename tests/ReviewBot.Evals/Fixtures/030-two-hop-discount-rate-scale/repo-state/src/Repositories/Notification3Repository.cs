namespace Scale.Repositories;

using Scale.Domain;

public sealed class Notification3Repository
{
    private readonly Dictionary<int, Notification3Record> items = new();

    public Notification3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Notification3Record> All() => this.items.Values;

    public void Upsert(Notification3Record record) => this.items[record.Id] = record;
}
