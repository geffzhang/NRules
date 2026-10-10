using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NRules;
using NRules.Json;
using NRules.Persistence.EntityFrameworkCore;
using NRules.Persistence.EntityFrameworkCore.DependencyInjection;
using NRules.Persistence.EntityFrameworkCore.Options;
using NRules.Samples.Persistence.Postgres;
using NRules.Samples.Persistence.Postgres.Domain;

var builder = Host.CreateApplicationBuilder();

builder.Configuration.AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables();

var connectionString = builder.Configuration.GetConnectionString("Rules")
    ?? throw new InvalidOperationException("Connection string 'Rules' not configured");

builder.Services.AddNRulesEfCoreStore(
    options =>
    {
        options.TypeResolver = new TypeResolver();
        options.TypeResolver.RegisterDefaultAliases();
        options.TypeResolver.RegisterAlias("Customer", typeof(Customer));
        options.TypeResolver.RegisterAlias("Order", typeof(Order));
        options.TypeResolver.RegisterAlias("Context", typeof(NRules.RuleModel.IContext));
    },
    db => db.UseNpgsql(connectionString));

builder.Services.AddNRulesReloader(options =>
{
    options.PollInterval = TimeSpan.FromSeconds(5);
});

builder.Services.UsePostgresNotify(options => options.ConnectionString = connectionString);
builder.Services.AddSingleton(TimeProvider.System);

using var host = builder.Build();

// Migrate and seed before the hosted reloader performs its bootstrap load.
await using (var scope = host.Services.CreateAsyncScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RulesDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();

    var store = scope.ServiceProvider.GetRequiredService<IRuleStore>();
    await RulesSeed.EnsureSeededAsync(store, CancellationToken.None);
}

await host.StartAsync();

var provider = host.Services.GetRequiredService<IRuleSessionFactoryProvider>();
var factory = await provider.WaitForReadyAsync();
var customer = new Customer("VIP John");

RunRules(factory);

Console.WriteLine("Rules are live. Update the rule in PostgreSQL, then press Enter to evaluate again; Ctrl+C exits.");
var stoppingToken = host.Services
    .GetRequiredService<IHostApplicationLifetime>()
    .ApplicationStopping;
try
{
    while (await Console.In.ReadLineAsync(stoppingToken) is not null)
    {
        RunRules(provider.Current);
    }
}
catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
{
}

await host.StopAsync();

void RunRules(ISessionFactory sessionFactory)
{
    var session = sessionFactory.CreateSession();
    session.Insert(customer);
    session.Insert(new Order(1, customer, 200));
    session.Fire();
}
