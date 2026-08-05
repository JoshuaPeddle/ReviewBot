namespace Scale.Repositories;

using Scale.Domain;

public sealed class Campaign2Repository
{
    private readonly Dictionary<int, Campaign2Record> items = new();

    public Campaign2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Campaign2Record> All() => this.items.Values;

    public void Upsert(Campaign2Record record) => this.items[record.Id] = record;
}
