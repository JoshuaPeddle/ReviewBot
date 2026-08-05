namespace Scale.Repositories;

using Scale.Domain;

public sealed class Channel1Repository
{
    private readonly Dictionary<int, Channel1Record> items = new();

    public Channel1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Channel1Record> All() => this.items.Values;

    public void Upsert(Channel1Record record) => this.items[record.Id] = record;
}
