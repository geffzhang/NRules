using System.Text.Json;
using NRules.Json;
using NRules.Persistence.EntityFrameworkCore.Options;

namespace NRules.Persistence.EntityFrameworkCore.Serialization;

public static class RuleJsonConverterFactory
{
    public static JsonSerializerOptions Create(EFCoreStoreOptions opt)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        opt.ConfigureSerializer?.Invoke(options);
        RuleSerializer.Setup(options, opt.TypeResolver);
        return options;
    }

    public static JsonSerializerOptions Create(TypeResolver typeResolver)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        RuleSerializer.Setup(options, typeResolver);
        return options;
    }
}
