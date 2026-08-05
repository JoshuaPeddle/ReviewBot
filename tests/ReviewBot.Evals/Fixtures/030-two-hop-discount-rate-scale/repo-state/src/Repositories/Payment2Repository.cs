namespace Scale.Repositories;

using Scale.Domain;

public sealed class Payment2Repository
{
    private readonly Dictionary<int, Payment2Record> items = new();

    public Payment2Record? Find(int id) => this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<Payment2Record> All() => this.items.Values;

    public void Upsert(Payment2Record record) => this.items[record.Id] = record;
}
