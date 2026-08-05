namespace Scale.Repositories;

using Scale.Domain;

public sealed class Channel3Repository
{
    private readonly Dictionary<int, Channel3Record> items = new();

    public Channel3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Channel3Record> All() => this.items.Values;

    public void Upsert(Channel3Record record) => this.items[record.Id] = record;
}
