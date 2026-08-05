namespace Scale.Services;

using Scale.Domain;
using Scale.Repositories;

public sealed class Reservation2Service
{
    private readonly Reservation2Repository repository;

    public Reservation2Service(Reservation2Repository repository) => this.repository = repository;

    public Reservation2Record? Get(int id) => this.repository.Find(id);

    public decimal TotalAmount() => this.repository.All().Sum(item => item.Amount);
}
