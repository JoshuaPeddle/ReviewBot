namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Ledger3Service
{
    private readonly Ledger3Repository repository;

    public Ledger3Service(Ledger3Repository repository) => this.repository = repository;

    public Ledger3Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
