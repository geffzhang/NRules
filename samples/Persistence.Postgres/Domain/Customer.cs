namespace NRules.Samples.Persistence.Postgres.Domain;

public class Customer
{
    public string Name { get; }

    public Customer(string name) => Name = name;

    public override string ToString() => $"Customer({Name})";
}
