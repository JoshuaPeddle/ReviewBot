namespace Scale.Repositories;

using Scale.Domain;

public sealed class Channel2Repository
{
    private readonly Dictionary<int, Channel2Record> items = new();

    public Channel2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Channel2Record> All() => this.items.Values;

    public void Upsert(Channel2Record record) => this.items[record.Id] = record;
}
