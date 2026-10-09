using Xunit;

namespace NRules.Persistence.EntityFrameworkCore.Tests;

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
