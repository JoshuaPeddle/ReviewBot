namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Reservation1Service
{
    private readonly Reservation1Repository repository;

    public Reservation1Service(Reservation1Repository repository) => this.repository = repository;

    public Reservation1Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
