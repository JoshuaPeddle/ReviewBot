namespace Scale.Repositories;

using Scale.Domain;

public sealed class Campaign1Repository
{
    private readonly Dictionary<int, Campaign1Record> items = new();

    public Campaign1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Campaign1Record> All() => this.items.Values;

    public void Upsert(Campaign1Record record) => this.items[record.Id] = record;
}
