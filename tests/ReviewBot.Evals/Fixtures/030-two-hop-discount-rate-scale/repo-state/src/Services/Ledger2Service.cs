namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Ledger2Service
{
    private readonly Ledger2Repository repository;

    public Ledger2Service(Ledger2Repository repository) => this.repository = repository;

    public Ledger2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
