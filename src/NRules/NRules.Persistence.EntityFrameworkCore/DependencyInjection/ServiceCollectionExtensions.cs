using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NRules.Json;
using NRules.Persistence.EntityFrameworkCore.Options;
using NRules.Persistence.EntityFrameworkCore.Reloading;
using NRules.Persistence.EntityFrameworkCore.Serialization;

namespace NRules.Persistence.EntityFrameworkCore.DependencyInjection;

public static class NRulesEfCoreServiceCollectionExtensions
{
    public static IServiceCollection AddNRulesEfCoreStore(
        this IServiceCollection services,
        Action<EFCoreStoreOptions> configure,
        Action<DbContextOptionsBuilder>? configureDb = null)
    {
        services.AddOptions<EFCoreStoreOptions>().Configure(configure);
        services.AddSingleton(serviceProvider =>
            RuleJsonConverterFactory.Create(serviceProvider.GetRequiredService<IOptions<EFCoreStoreOptions>>().Value));
        services.AddDbContextFactory<RulesDbContext>(builder => configureDb?.Invoke(builder));
        services.AddSingleton<IRuleStore, EFCoreRuleStore>();
        services.AddSingleton<IRuleSessionFactoryProvider, RuleSessionFactoryProvider>();
        return services;
    }

    public static IServiceCollection AddNRulesReloader(
        this IServiceCollection services,
        Action<ReloaderOptions>? configure = null)
    {
        services.AddOptions<ReloaderOptions>().Configure(options =>
        {
            configure?.Invoke(options);
            options.PollInterval ??= TimeSpan.FromSeconds(30);
            options.PollJitter ??= TimeSpan.FromSeconds(3);
        });

        services.AddOptions<PostgresNotifyOptions>();
        services.AddSingleton(serviceProvider => new RulesReloader(
            serviceProvider.GetRequiredService<IRuleStore>(),
            serviceProvider.GetRequiredService<IRuleSessionFactoryProvider>(),
            serviceProvider.GetRequiredService<IOptions<ReloaderOptions>>().Value,
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<ILoggerFactory>(),
            serviceProvider.GetRequiredService<IOptions<PostgresNotifyOptions>>().Value));
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<RulesReloader>());
        return services;
    }

    public static IServiceCollection UsePostgresNotify(
        this IServiceCollection services,
        Action<PostgresNotifyOptions>? configure = null)
    {
        services.AddOptions<PostgresNotifyOptions>().Configure(options => configure?.Invoke(options));
        services.Configure<ReloaderOptions>(options => options.UseNotify = true);
        return services;
    }
}
