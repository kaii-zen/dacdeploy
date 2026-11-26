using System.Text.RegularExpressions;
using Microsoft.SqlServer.Dac.Deployment;
using Microsoft.SqlServer.Dac.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using ColumnType = Microsoft.SqlServer.Dac.Model.ColumnType;
using Index = Microsoft.SqlServer.Dac.Model.Index;

namespace DacDeploy;

/// <summary>
/// A deployment contributor that leaves in place what the target database has and the project doesn't, and
/// reports it. DacFx already leaves objects alone (DropObjectsNotInSource is off), but drops columns, and the
/// indexes and constraints that go with them; this takes those drops out of the plan. A kept column that is NOT NULL
/// without a default is made nullable, so inserts that leave it out keep working.
/// <para>
/// With the <see cref="PruneArgument"/> argument (and DropObjectsNotInSource on), it drops what isn't in the project
/// instead, and takes out every other drop DropObjectsNotInSource adds: users, roles, other schemas, the data scripts' records.
/// </para>
/// <para>
/// It records in the deployment's <see cref="DeploymentReport"/> what isn't in the project, and the tables it rewrote
/// that still change (DacFx's deploy report leaves rewritten steps out).
/// </para>
/// </summary>
[ExportDeploymentPlanModifier(Id, "1.0.0.0")]
public sealed partial class KeepRemoved : DeploymentPlanModifier
{
    public const string Id = "DacDeploy.KeepRemoved";

    public const string PruneArgument = "DacDeploy.KeepRemoved.Prune";

    // What a project holds: security and storage are environment-specific.
    private static readonly ModelTypeClass[] SchemaObjectTypes =
    [
        Table.TypeClass, View.TypeClass, Procedure.TypeClass, ScalarFunction.TypeClass, TableValuedFunction.TypeClass,
        TableType.TypeClass, UserDefinedType.TypeClass, Synonym.TypeClass,
    ];

    private static readonly ModelTypeClass[] ProjectTypes =
    [
        .. SchemaObjectTypes, DmlTrigger.TypeClass, Index.TypeClass, Column.TypeClass, DefaultConstraint.TypeClass,
        CheckConstraint.TypeClass, ForeignKeyConstraint.TypeClass, UniqueConstraint.TypeClass, PrimaryKeyConstraint.TypeClass,
    ];

    private static readonly Sql100ScriptGenerator Generator = new(new SqlScriptGeneratorOptions { SqlVersion = SqlVersion.Sql100 });

    [GeneratedRegex(@"The column (\[[^\]]+\]\.\[[^\]]+\]\.\[[^\]]+\]) is being dropped")]
    private static partial Regex DataLossCheck();

    protected override void OnExecute(DeploymentPlanContributorContext context)
    {
        var prune = context.Arguments.TryGetValue(PruneArgument, out var value) && value == "true";
        var report = DeploymentReport.For(context.Arguments);
        var notInProject = NotInProject(context);
        var steps = PlanSteps.All(context.PlanHandle);

        // Every drop of something DacFx found only in the target: drop exactly what isn't in the project when
        // pruning, and nothing of it otherwise.
        foreach (var drop in steps.OfType<DropElementStep>())
        {
            var name = drop.TargetElement?.Name.ToString();
            if (name != null && context.ComparisonResult.ElementsToDrop.Any(e => e.Name.ToString() == name) && prune != notInProject.ContainsKey(name))
            {
                Remove(context.PlanHandle, drop);
            }
        }

        if (prune)
        {
            foreach (var element in notInProject.Values)
            {
                report?.Add(new NotInProjectItem(element.ObjectType.Name, element.Name.ToString(), Dropped: true, MadeNullable: false));
            }

            return;
        }

        var kept = notInProject.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nullable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        foreach (var step in steps)
        {
            var rewritten = step switch
            {
                SqlTableMigrationStep rebuild => KeepInRebuild(context, rebuild, notInProject, nullable),
                AlterElementStep { SourceElement: var table } alter when table?.ObjectType == Table.TypeClass => KeepInAlter(alter, kept),
                _ => null,
            };
            if (rewritten == null)
            {
                continue;
            }

            if (rewritten.Count > 0)
            {
                AddBefore(context.PlanHandle, step, new DeploymentScriptStep(rewritten));
                changed.Add(((AlterElementStep)step).SourceElement.Name.ToString());
            }

            Remove(context.PlanHandle, step);
        }

        changed.AddRange(MakeNullable(context, notInProject, nullable, steps));

        // DacFx checks for rows before dropping a column; the kept columns aren't dropped.
        foreach (var check in steps.OfType<DeploymentScriptStep>())
        {
            var columns = DataLossCheck().Matches(string.Join("\n", check.GenerateTSQL())).Select(m => m.Groups[1].Value).ToList();
            if (columns.Count > 0 && columns.All(kept.Contains))
            {
                Remove(context.PlanHandle, check);
            }
        }

        foreach (var element in notInProject.Values)
        {
            report?.Add(new NotInProjectItem(
                element.ObjectType.Name, element.Name.ToString(), Dropped: false, MadeNullable: nullable.Contains(element.Name.ToString())));
        }

        foreach (var table in changed.Distinct())
        {
            report?.AddRewrittenTable(table);
        }
    }

    /// <summary>What DacFx found only in the target, in the project's schemas, by name; without the parts of something already listed.</summary>
    private static Dictionary<string, TSqlObject> NotInProject(DeploymentPlanContributorContext context)
    {
        var schemas = context.Source.GetObjects(DacQueryScopes.UserDefined, SchemaObjectTypes)
            .Select(o => o.Name.Parts[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toDrop = context.ComparisonResult.ElementsToDrop
            .Where(e => ProjectTypes.Contains(e.ObjectType) && e.Name.Parts.Count > 1 && schemas.Contains(e.Name.Parts[0]))
            .ToList();
        var names = toDrop.Select(e => e.Name.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A view's columns come and go with its definition; only a table's are kept.
        return toDrop
            .Where(e => e.ObjectType != Column.TypeClass || Owner(e)?.ObjectType == Table.TypeClass)
            .Where(e => Owner(e) is not { } owner || !names.Contains(owner.Name.ToString()))
            .ToDictionary(e => e.Name.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The table or view an element belongs to, if it belongs to one.</summary>
    private static TSqlObject? Owner(TSqlObject element) =>
        element.GetParent(DacQueryScopes.All) ?? element.ObjectType switch
        {
            var t when t == DefaultConstraint.TypeClass => element.GetReferenced(DefaultConstraint.Host).FirstOrDefault(),
            var t when t == CheckConstraint.TypeClass => element.GetReferenced(CheckConstraint.Host).FirstOrDefault(),
            var t when t == ForeignKeyConstraint.TypeClass => element.GetReferenced(ForeignKeyConstraint.Host).FirstOrDefault(),
            var t when t == UniqueConstraint.TypeClass => element.GetReferenced(UniqueConstraint.Host).FirstOrDefault(),
            var t when t == PrimaryKeyConstraint.TypeClass => element.GetReferenced(PrimaryKeyConstraint.Host).FirstOrDefault(),
            var t when t == Index.TypeClass => element.GetReferenced(Index.IndexedObject).FirstOrDefault(),
            var t when t == DmlTrigger.TypeClass => element.GetReferenced(DmlTrigger.TriggerObject).FirstOrDefault(),
            _ => null,
        };

    /// <summary>
    /// The alter's statements without the drops of kept columns, constraints and indexes; null when it drops none of
    /// them, empty when nothing but its PRINT is left.
    /// </summary>
    private static List<string>? KeepInAlter(AlterElementStep alter, HashSet<string> kept)
    {
        var table = alter.SourceElement.Name;
        var schema = $"[{table.Parts[0]}]";
        var rewrote = false;
        var batches = alter.GenerateTSQL().Select(Parse).ToList();
        foreach (var statement in batches.SelectMany(s => s.Batches).SelectMany(b => b.Statements))
        {
            if (statement is not AlterTableDropTableElementStatement drop)
            {
                continue;
            }

            var elements = drop.AlterTableDropTableElements;
            foreach (var element in elements.Where(e => kept.Contains(e.TableElementType == TableElementType.Constraint
                ? $"{schema}.[{e.Name.Value}]"
                : $"{table}.[{e.Name.Value}]")).ToList())
            {
                elements.Remove(element);
                rewrote = true;
            }
        }

        if (!rewrote)
        {
            return null;
        }

        foreach (var batch in batches.SelectMany(s => s.Batches).Select(b => b.Statements))
        {
            foreach (var empty in batch.OfType<AlterTableDropTableElementStatement>().Where(d => d.AlterTableDropTableElements.Count == 0).ToList())
            {
                batch.Remove(empty);
            }
        }

        var left = batches.SelectMany(s => s.Batches).SelectMany(b => b.Statements);
        return left.All(s => s is PrintStatement || IsErrorHandling(s)) ? [] : [.. batches.Select(Generate)];
    }

    /// <summary>
    /// The rebuild with the target's kept columns in the new table (nullable) and copied over; null when the table has
    /// none. Refuses when the table has other kept parts, which the rebuild would lose.
    /// </summary>
    private static List<string>? KeepInRebuild(DeploymentPlanContributorContext context, SqlTableMigrationStep rebuild, Dictionary<string, TSqlObject> notInProject, HashSet<string> nullable)
    {
        var table = rebuild.SourceTable.Name.ToString();
        var parts = notInProject.Values.Where(e => Owner(e)?.Name.ToString() == table).ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        if (parts.Any(p => p.ObjectType != Column.TypeClass))
        {
            throw new DeploymentFailedException(
                $"{table} has to be rebuilt, which would lose what it has that the project doesn't: " +
                $"{string.Join(", ", parts.Select(p => p.Name))}. Put them back in the project for this release, and drop them in a later maintenance prune.");
        }

        var target = context.Target.GetObject(Table.TypeClass, rebuild.SourceTable.Name, DacQueryScopes.UserDefined);
        var kept = parts.Select(p => p.Name.Parts[^1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Columns can't be scripted on their own; take their definitions from the target table's CREATE TABLE.
        var columns = Parse(target.GetScript()).Batches.SelectMany(b => b.Statements).OfType<CreateTableStatement>().Single()
            .Definition.ColumnDefinitions.Where(c => kept.Contains(c.ColumnIdentifier.Value)).ToList();
        foreach (var column in columns.Where(c => NeedsNullable(context, target, c.ColumnIdentifier.Value)))
        {
            MakeNullable(column);
            nullable.Add($"{table}.[{column.ColumnIdentifier.Value}]");
        }

        var batches = rebuild.GenerateTSQL().Select(Parse).ToList();
        foreach (var batch in batches)
        {
            batch.Accept(new AddColumns(columns));
        }

        return [.. batches.Select(Generate)];
    }

    /// <summary>Makes the kept table columns that need it nullable, unless a rebuild already did; returns the tables it alters.</summary>
    private List<string> MakeNullable(DeploymentPlanContributorContext context, Dictionary<string, TSqlObject> notInProject, HashSet<string> nullable, List<DeploymentStep> steps)
    {
        var tables = new List<string>();
        // Inside the deployment's transaction (IncludeTransactionalScripts), else at the end of the alters.
        var end = (DeploymentStep?)steps.OfType<SqlEndTransactionStep>().FirstOrDefault() ?? steps.OfType<SqlEndAltersStep>().FirstOrDefault();
        var columns = notInProject.Values
            .Where(e => e.ObjectType == Column.TypeClass && !nullable.Contains(e.Name.ToString()))
            .Select(column => (Column: column, Table: Owner(column)!))
            .Where(c => c.Table?.ObjectType == Table.TypeClass && NeedsNullable(context, c.Table, c.Column.Name.Parts[^1]))
            .ToList();
        foreach (var (column, table) in columns)
        {
            // A primary key column can't be NULL; a kept key still covering the column rules it out.
            if (notInProject.Values.FirstOrDefault(e => e.ObjectType == PrimaryKeyConstraint.TypeClass
                    && e.GetReferenced(PrimaryKeyConstraint.Columns).Any(c => c.Name.ToString() == column.Name.ToString())) is { } key)
            {
                throw new DeploymentFailedException(
                    $"{column.Name} isn't in the project and is NOT NULL without a default, so it would be made nullable, but it's part of " +
                    $"the primary key {key.Name}, which isn't in the project either. Put them back in the project for this release, " +
                    "and drop them in a later maintenance prune.");
            }

            var definition = Parse(table.GetScript()).Batches.SelectMany(b => b.Statements).OfType<CreateTableStatement>().Single()
                .Definition.ColumnDefinitions.Single(c => string.Equals(c.ColumnIdentifier.Value, column.Name.Parts[^1], StringComparison.OrdinalIgnoreCase));
            var collation = definition.Collation is { } c ? $" COLLATE {c.Value}" : "";
            var sql = $"ALTER TABLE {table.Name} ALTER COLUMN [{column.Name.Parts[^1]}] {Generate(definition.DataType)}{collation} NULL;";
            var step = new DeploymentScriptStep([$"PRINT N'Making {column.Name} nullable: it isn''t in the project...';", sql]);
            if (end != null)
            {
                AddBefore(context.PlanHandle, end, step);
            }
            else
            {
                AddAfter(context.PlanHandle, context.PlanHandle.Tail, step);
            }

            nullable.Add(column.Name.ToString());
            tables.Add(table.Name.ToString());
        }

        return tables;
    }

    /// <summary>Whether inserts that leave the target table's column out would fail.</summary>
    private static bool NeedsNullable(DeploymentPlanContributorContext context, TSqlObject table, string column)
    {
        var element = table.GetReferenced(Table.Columns).Single(c => string.Equals(c.Name.Parts[^1], column, StringComparison.OrdinalIgnoreCase));
        if (element.GetProperty<bool>(Column.Nullable) || element.GetProperty<bool>(Column.IsIdentity)
            || element.GetMetadata<ColumnType>(Column.ColumnType) != ColumnType.Column)
        {
            return false;
        }

        var type = element.GetReferenced(Column.DataType).FirstOrDefault()?.Name.Parts[^1];
        if (string.Equals(type, "timestamp", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "rowversion", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = element.Name.ToString();
        return !context.Target.GetObjects(DacQueryScopes.UserDefined, DefaultConstraint.TypeClass)
            .Any(d => d.GetReferenced(DefaultConstraint.TargetColumn).Any(c => c.Name.ToString() == name));
    }

    private static void MakeNullable(ColumnDefinition column)
    {
        foreach (var constraint in column.Constraints.OfType<NullableConstraintDefinition>().ToList())
        {
            column.Constraints.Remove(constraint);
        }

        column.Constraints.Add(new NullableConstraintDefinition { Nullable = true });
    }

    /// <summary>
    /// Whether a statement is DacFx's error handling, which it adds after each step of a transactional deployment
    /// (IncludeTransactionalScripts) and which changes nothing on its own.
    /// </summary>
    private static bool IsErrorHandling(TSqlStatement statement) =>
        Generate(statement) is var sql && (sql.Contains("@@TRANCOUNT", StringComparison.Ordinal) || sql.Contains("#tmpErrors", StringComparison.Ordinal));

    private static TSqlScript Parse(string sql)
    {
        var fragment = new TSql100Parser(initialQuotedIdentifiers: true).Parse(new StringReader(sql), out var errors);
        return errors.Count == 0
            ? (TSqlScript)fragment
            : throw new DeploymentFailedException($"Couldn't parse DacFx's script: {errors[0].Message}\n{sql}");
    }

    private static string Generate(TSqlFragment fragment)
    {
        Generator.GenerateScript(fragment, out var sql);
        return sql;
    }

    /// <summary>Adds the kept columns to the rebuild's new table and to the INSERT ... SELECT that fills it.</summary>
    private sealed class AddColumns(List<ColumnDefinition> columns) : TSqlFragmentVisitor
    {
        private const string NewTablePrefix = "tmp_ms_xx_";

        public override void Visit(CreateTableStatement node)
        {
            if (node.SchemaObjectName.BaseIdentifier.Value.StartsWith(NewTablePrefix, StringComparison.Ordinal))
            {
                foreach (var column in columns)
                {
                    node.Definition.ColumnDefinitions.Add(column);
                }
            }
        }

        public override void Visit(InsertStatement node)
        {
            if (node.InsertSpecification.Target is not NamedTableReference { SchemaObject.BaseIdentifier.Value: var target }
                || !target.StartsWith(NewTablePrefix, StringComparison.Ordinal)
                || node.InsertSpecification.InsertSource is not SelectInsertSource { Select: QuerySpecification select })
            {
                return;
            }

            foreach (var column in columns)
            {
                node.InsertSpecification.Columns.Add(Reference(column));
                select.SelectElements.Add(new SelectScalarExpression { Expression = Reference(column) });
            }
        }

        private static ColumnReferenceExpression Reference(ColumnDefinition column) => new()
        {
            MultiPartIdentifier = new MultiPartIdentifier
            {
                Identifiers = { new Identifier { Value = column.ColumnIdentifier.Value, QuoteType = QuoteType.SquareBracket } },
            },
        };
    }
}
