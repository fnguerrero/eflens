using System.Globalization;
using System.Xml.Linq;

namespace EfLens.Providers;

/// <summary>Turns SQL Server's showplan XML into the shared <see cref="QueryPlan"/> shape.</summary>
internal static class ShowPlan
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static QueryPlan Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var statement = document.Descendants(Ns + "StmtSimple").FirstOrDefault()
            ?? throw new QueryFailedException("The plan has no statement.");
        var queryPlan = statement.Element(Ns + "QueryPlan");

        var roots = queryPlan?.Elements(Ns + "RelOp").Select(Node).ToList() ?? [];
        var notes = new List<string>();

        foreach (var group in queryPlan?.Element(Ns + "MissingIndexes")?.Elements(Ns + "MissingIndexGroup") ?? [])
        {
            var impact = Number(group.Attribute("Impact"));
            foreach (var index in group.Elements(Ns + "MissingIndex"))
            {
                string Columns(params string[] usages) => string.Join(", ", index.Elements(Ns + "ColumnGroup")
                    .Where(g => usages.Contains((string?)g.Attribute("Usage")))
                    .SelectMany(g => g.Elements(Ns + "Column"))
                    .Select(c => Unbracket((string?)c.Attribute("Name"))));

                var keys = Columns("EQUALITY", "INEQUALITY");
                var include = Columns("INCLUDE");
                notes.Add($"Missing index (estimated impact {impact?.ToString("0.#", CultureInfo.InvariantCulture) ?? "?"}%): " +
                    $"{Unbracket((string?)index.Attribute("Schema"))}.{Unbracket((string?)index.Attribute("Table"))} ({keys})" +
                    (include.Length > 0 ? $" INCLUDE ({include})" : ""));
            }
        }

        foreach (var warning in statement.Descendants(Ns + "Warnings").SelectMany(w => w.Elements()).DistinctBy(w => w.ToString()))
        {
            var detail = (string?)warning.Attribute("Expression") ?? (string?)warning.Attribute("ConvertIssue");
            notes.Add($"Warning: {warning.Name.LocalName}{(detail is null ? "" : $" ({detail})")}");
        }

        return new QueryPlan(Number(statement.Attribute("StatementSubTreeCost")), roots, notes);
    }

    private static PlanNode Node(XElement relOp)
    {
        var physical = (string?)relOp.Attribute("PhysicalOp") ?? "?";
        var logical = (string?)relOp.Attribute("LogicalOp");
        var operation = logical is null || logical == physical ? physical : $"{physical} ({logical})";

        // The operator element (IndexScan, NestedLoops, ...) holds the object and the child operators.
        var target = relOp.Elements().Elements(Ns + "Object").FirstOrDefault();
        string? table = null;
        if (target is not null)
        {
            table = $"{Unbracket((string?)target.Attribute("Schema"))}.{Unbracket((string?)target.Attribute("Table"))}";
            if ((string?)target.Attribute("Index") is { } index)
                table += $" ({Unbracket(index)})";
        }

        return new PlanNode(
            operation,
            table,
            Number(relOp.Attribute("EstimateRows")),
            Number(relOp.Attribute("EstimatedTotalSubtreeCost")),
            [.. relOp.Elements().Elements(Ns + "RelOp").Select(Node)]);
    }

    private static double? Number(XAttribute? attribute) =>
        double.TryParse((string?)attribute, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? Math.Round(value, 4) : null;

    private static string Unbracket(string? name) => (name ?? "").Trim('[', ']');
}
