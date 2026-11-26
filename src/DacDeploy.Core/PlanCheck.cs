using System.Security.Cryptography;
using System.Text;
using Microsoft.SqlServer.Dac.Deployment;

namespace DacDeploy;

/// <summary>
/// A deployment contributor that fingerprints the final plan: the SHA-256 of every step's T-SQL, recorded in the
/// deployment's <see cref="DeploymentReport"/>. Given <see cref="ExpectedArgument"/>, it stops the
/// deployment unless the plan has that fingerprint, so Publish runs only the plan that was reviewed.
/// <para>
/// DacFx runs contributors in the order they're listed; <see cref="Deployer.Contributors"/> lists it last, so it sees the
/// others' changes.
/// </para>
/// </summary>
[ExportDeploymentPlanModifier(Id, "1.0.0.0")]
public sealed class PlanCheck : DeploymentPlanModifier
{
    public const string Id = "DacDeploy.PlanCheck";

    public const string ExpectedArgument = "DacDeploy.PlanCheck.Expected";

    protected override void OnExecute(DeploymentPlanContributorContext context)
    {
        var fingerprint = Fingerprint(context.PlanHandle);
        if (DeploymentReport.For(context.Arguments) is { } report)
        {
            report.Fingerprint = fingerprint;
        }

        if (context.Arguments.TryGetValue(ExpectedArgument, out var expected) && expected != fingerprint)
        {
            throw new DeploymentFailedException(
                "The plan is out of date: the database, the project, Data/ or Migrations/ changed since it was made. Plan again and review it.");
        }
    }

    private static string Fingerprint(DeploymentPlanHandle plan)
    {
        var sql = new StringBuilder();
        for (var step = plan.Head; step != null; step = step.Next)
        {
            foreach (var batch in step.GenerateTSQL() ?? [])
            {
                // A separator no batch holds, so moving text between batches changes the fingerprint.
                sql.Append(batch).Append('\0');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ToString())));
    }
}
