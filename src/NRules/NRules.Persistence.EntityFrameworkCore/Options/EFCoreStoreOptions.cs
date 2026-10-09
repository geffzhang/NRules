using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using NRules.Json;

namespace NRules.Persistence.EntityFrameworkCore.Options;

public class EFCoreStoreOptions
{
    [Required]
    public TypeResolver TypeResolver { get; set; } = default!;

    public Action<JsonSerializerOptions>? ConfigureSerializer { get; set; }
}
