namespace Scale.Repositories;

using Scale.Domain;

public sealed class Campaign3Repository
{
    private readonly Dictionary<int, Campaign3Record> items = new();

    public Campaign3Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Campaign3Record> All() => this.items.Values;

    public void Upsert(Campaign3Record record) => this.items[record.Id] = record;
}
