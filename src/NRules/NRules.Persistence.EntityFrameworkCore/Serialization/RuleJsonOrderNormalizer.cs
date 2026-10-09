using System.Text.Json;
using System.Text.Json.Nodes;

namespace NRules.Persistence.EntityFrameworkCore.Serialization;

internal static class RuleJsonOrderNormalizer
{
    private static readonly string[] RuleDefinitionOrder =
    [
        "name", "description", "priority", "repeatability", "tags", "properties",
        "dependencies", "leftHandSide", "filters", "rightHandSide"
    ];

    public static string Normalize(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new JsonException("Rule definition JSON was null.");
        return NormalizeNode(node, "ruleDefinition").ToJsonString();
    }

    private static JsonNode NormalizeNode(JsonNode node, string context)
    {
        if (node is JsonArray array)
        {
            var normalizedArray = new JsonArray();
            foreach (var child in array)
            {
                if (child is not null)
                    normalizedArray.Add(NormalizeNode(child, context));
                else
                    normalizedArray.Add((JsonNode?)null);
            }

            return normalizedArray;
        }

        if (node is not JsonObject obj)
            return node.DeepClone();

        var order = GetPropertyOrder(obj, context);
        var normalized = new JsonObject();
        foreach (var property in obj.OrderBy(pair => GetIndex(order, pair.Key)))
        {
            normalized[property.Key] = property.Value is null
                ? null
                : NormalizeNode(property.Value, GetChildContext(property.Key, context));
        }

        return normalized;
    }

    private static string[] GetPropertyOrder(JsonObject obj, string context)
    {
        if (obj.ContainsKey("elementType"))
        {
            var elementType = obj["elementType"]?.GetValue<string>();
            return elementType switch
            {
                "And" or "Or" => ["elementType", "childElements"],
                "Exists" or "Not" => ["elementType", "source"],
                "Aggregate" => ["elementType", "name", "resultType", "expressions", "customFactoryType", "source"],
                "Binding" => ["elementType", "resultType", "expression"],
                "Pattern" => ["elementType", "name", "type", "expressions", "source"],
                "Dependency" => ["elementType", "name", "type", "serviceType"],
                "Filter" => ["elementType", "filterType", "expression"],
                "ForAll" => ["elementType", "basePattern", "patterns"],
                _ => ["elementType"]
            };
        }

        if (obj.ContainsKey("nodeType"))
        {
            var nodeType = obj["nodeType"]?.GetValue<string>();
            return nodeType switch
            {
                "Lambda" => ["nodeType", "type", "parameters", "body"],
                "Parameter" => ["nodeType", "name", "type"],
                "Constant" => ["nodeType", "type", "value"],
                "MemberAccess" => ["nodeType", "memberType", "name", "declaringType", "expression"],
                "Call" => ["nodeType", "object", "methodName", "declaringType", "genericTypeArguments", "arguments"],
                "Equal" or "NotEqual" or "GreaterThanOrEqual" or "GreaterThan" or "LessThanOrEqual" or
                    "LessThan" or "AndAlso" or "OrElse" or "And" or "Or" or "ExclusiveOr" or "Add" or
                    "AddChecked" or "Divide" or "Modulo" or "Multiply" or "MultiplyChecked" or "Power" or
                    "Subtract" or "SubtractChecked" or "Coalesce" or "ArrayIndex" or "LeftShift" or
                    "RightShift" or "Assign"
                    => ["nodeType", "left", "right", "methodName", "declaringType", "genericTypeArguments"],
                "Not" or "Negate" or "NegateChecked" or "UnaryPlus" or "Convert" or "ConvertChecked" or
                    "TypeAs"
                    => ["nodeType", "operand", "type", "methodName", "declaringType", "genericTypeArguments"],
                "Invoke" => ["nodeType", "expression", "arguments"],
                "TypeIs" => ["nodeType", "expression", "typeOperand"],
                "New" => ["nodeType", "declaringType", "arguments"],
                "NewArrayInit" => ["nodeType", "elementType", "expressions"],
                "MemberInit" => ["nodeType", "declaringType", "arguments", "bindings"],
                "ListInit" => ["nodeType", "declaringType", "arguments", "initializers"],
                "Conditional" => ["nodeType", "test", "ifTrue", "ifFalse"],
                "Default" => ["nodeType", "type"],
                "Block" => ["nodeType", "type", "variables", "expressions"],
                _ => ["nodeType"]
            };
        }

        if (obj.ContainsKey("bindingType"))
            return ["bindingType", "memberType", "name", "declaringType", "expression"];
        if (obj.ContainsKey("memberType"))
            return ["memberType", "name", "declaringType"];
        if (obj.ContainsKey("methodName"))
            return ["methodName", "declaringType", "genericTypeArguments", "arguments"];
        if (context == "ruleDefinition")
            return RuleDefinitionOrder;
        if (context == "ruleProperty")
            return ["name", "type", "value"];
        if (context == "namedExpression")
            return ["name", "expression"];
        if (context == "action")
            return ["actionTrigger", "expression"];
        if (context == "parameter")
            return ["name", "type"];

        return [];
    }

    private static string GetChildContext(string propertyName, string parentContext)
    {
        return propertyName switch
        {
            "properties" when parentContext == "ruleDefinition" => "ruleProperty",
            "expressions" => "namedExpression",
            "parameters" or "variables" => "parameter",
            "rightHandSide" or "initializers" => "action",
            "bindings" => "memberBinding",
            "arguments" or "childElements" or "dependencies" or "filters" or "patterns" => parentContext,
            "leftHandSide" or "source" or "basePattern" or "body" or "expression" or "left" or "right" or
                "operand" or "object" or "test" or "ifTrue" or "ifFalse" => "expressionOrElement",
            _ => "unknown"
        };
    }

    private static int GetIndex(string[] order, string propertyName)
    {
        var index = Array.IndexOf(order, propertyName);
        return index < 0 ? int.MaxValue : index;
    }
}
