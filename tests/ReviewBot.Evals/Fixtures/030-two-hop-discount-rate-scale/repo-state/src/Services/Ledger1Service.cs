namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Ledger1Service
{
    private readonly Ledger1Repository repository;

    public Ledger1Service(Ledger1Repository repository) => this.repository = repository;

    public Ledger1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
