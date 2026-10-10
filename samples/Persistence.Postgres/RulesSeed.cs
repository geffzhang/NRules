using System.Linq.Expressions;
using NRules.Persistence.EntityFrameworkCore;
using NRules.RuleModel;
using NRules.RuleModel.Builders;

namespace NRules.Samples.Persistence.Postgres;

public static class RulesSeed
{
    public static async Task EnsureSeededAsync(IRuleStore store, CancellationToken ct)
    {
        if ((await store.LoadAllAsync(ct)).Count > 0)
            return;

        await store.SaveRuleAsync(LargeOrder(), ct);
        await store.SaveRuleAsync(VipOrder(), ct);
    }

    private static IRuleDefinition LargeOrder()
    {
        var builder = new RuleBuilder();
        builder.Name("LargeOrderRule");
        builder.Priority(10);
        Expression<Func<Domain.Order, bool>> condition = o => o.Amount > 100;
        builder.LeftHandSide().Pattern(typeof(Domain.Order), "o").Condition(condition);
        Expression<Action<IContext, Domain.Order>> action =
            (context, o) => Console.WriteLine($"[LargeOrderRule] {o.Id}");
        builder.RightHandSide()
            .Action(action);
        return builder.Build();
    }

    private static IRuleDefinition VipOrder()
    {
        var builder = new RuleBuilder();
        builder.Name("VipOrderRule");
        builder.Priority(20);
        Expression<Func<Domain.Order, bool>> condition =
            o => o.Customer.Name.StartsWith("VIP");
        builder.LeftHandSide().Pattern(typeof(Domain.Order), "o").Condition(condition);
        Expression<Action<IContext, Domain.Order>> action =
            (context, o) => Console.WriteLine($"[VipOrderRule] {o.Id}");
        builder.RightHandSide()
            .Action(action);
        return builder.Build();
    }
}
