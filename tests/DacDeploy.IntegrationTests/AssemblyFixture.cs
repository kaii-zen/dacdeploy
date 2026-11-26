// One SQL Server for the whole run: each test class starting its own is more than a laptop can take.
[assembly: AssemblyFixture(typeof(DacDeploy.IntegrationTests.SampleDatabaseFixture))]
