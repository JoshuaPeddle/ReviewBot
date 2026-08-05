namespace Scale.Repositories;

using Scale.Domain;

public sealed class Ledger1Repository
{
    private readonly Dictionary<int, Ledger1Record> items = new();

    public Ledger1Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Ledger1Record> All() => this.items.Values;

    public void Upsert(Ledger1Record record) => this.items[record.Id] = record;
}
