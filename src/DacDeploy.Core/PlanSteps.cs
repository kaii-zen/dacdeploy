using Microsoft.SqlServer.Dac.Deployment;

namespace DacDeploy;

internal static class PlanSteps
{
    /// <summary>The plan's steps, as a list the caller can add to and remove from the plan while going through.</summary>
    public static List<DeploymentStep> All(DeploymentPlanHandle plan)
    {
        var steps = new List<DeploymentStep>();
        for (var step = plan.Head; step != null; step = step.Next)
        {
            steps.Add(step);
        }

        return steps;
    }
}
