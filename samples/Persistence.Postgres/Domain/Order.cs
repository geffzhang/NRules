namespace NRules.Samples.Persistence.Postgres.Domain;

public class Order
{
    public int Id { get; }
    public Customer Customer { get; }
    public double Amount { get; }

    public Order(int id, Customer customer, double amount)
    {
        Id = id;
        Customer = customer;
        Amount = amount;
    }

    public override string ToString() => $"Order({Id}, {Amount})";
}
