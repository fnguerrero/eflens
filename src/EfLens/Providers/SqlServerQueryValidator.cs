using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace EfLens.Providers;

/// <summary>
/// Accepts only one plain SELECT, using Microsoft's own T-SQL parser (ScriptDom) instead of keyword matching:
/// comments, string literals and CTEs cannot fool it. The query still runs inside a transaction that is always
/// rolled back; this check is what gives the agent a clear reason instead of a vague database error.
/// </summary>
public static class SqlServerQueryValidator
{
    public static void Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new QueryRejectedException("the query is empty.");

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);

        if (errors.Count > 0)
        {
            var e = errors[0];
            throw new QueryRejectedException($"T-SQL syntax error at line {e.Line}, column {e.Column}: {e.Message}.");
        }

        var statements = ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count == 0)
            throw new QueryRejectedException("the query has no statement.");
        if (statements.Count > 1)
            throw new QueryRejectedException($"found {statements.Count} statements; send exactly one.");

        if (statements[0] is not SelectStatement select)
            throw new QueryRejectedException($"{Describe(statements[0])} statements are not allowed.");
        if (select.Into is not null)
            throw new QueryRejectedException("SELECT ... INTO creates a table.");

        var guard = new ForbiddenConstructs();
        select.Accept(guard);
        if (guard.Reason is not null)
            throw new QueryRejectedException(guard.Reason);
    }

    /// <summary><c>DropTableStatement</c> becomes <c>DROP TABLE</c>.</summary>
    private static string Describe(TSqlStatement statement)
    {
        var name = statement.GetType().Name;
        if (name.EndsWith("Statement", StringComparison.Ordinal))
            name = name[..^"Statement".Length];
        return System.Text.RegularExpressions.Regex.Replace(name, "(?<!^)([A-Z])", " $1").ToUpperInvariant();
    }

    /// <summary>Things that are legal inside a SELECT but reach outside it or change state.</summary>
    private sealed class ForbiddenConstructs : TSqlFragmentVisitor
    {
        private static readonly HashSet<TableHintKind> LockingHints =
        [
            TableHintKind.XLock, TableHintKind.UpdLock, TableHintKind.TabLock, TableHintKind.TabLockX,
            TableHintKind.HoldLock, TableHintKind.Serializable, TableHintKind.RepeatableRead, TableHintKind.PagLock,
        ];

        public string? Reason { get; private set; }

        private void Reject(string reason) => Reason ??= reason;

        public override void Visit(OpenRowsetTableReference node) => Reject("OPENROWSET reaches outside the database.");
        public override void Visit(BulkOpenRowset node) => Reject("OPENROWSET(BULK ...) reads files on the server.");
        public override void Visit(InternalOpenRowset node) => Reject("OPENROWSET reaches outside the database.");
        public override void Visit(OpenRowsetCosmos node) => Reject("OPENROWSET reaches outside the database.");
        public override void Visit(OpenQueryTableReference node) => Reject("OPENQUERY runs SQL on a linked server.");
        public override void Visit(AdHocTableReference node) => Reject("OPENDATASOURCE reaches outside the database.");
        public override void Visit(NextValueForExpression node) => Reject("NEXT VALUE FOR advances a sequence.");

        public override void Visit(SchemaObjectName node)
        {
            if (node.ServerIdentifier is not null)
                Reject($"'{node.ServerIdentifier.Value}' is a linked server; only the connected database can be read.");
        }

        public override void Visit(TableHint node)
        {
            if (LockingHints.Contains(node.HintKind))
                Reject($"the locking hint {node.HintKind.ToString().ToUpperInvariant()} could block other sessions.");
        }
    }
}
