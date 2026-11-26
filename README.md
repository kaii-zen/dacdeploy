# dacdeploy

Plans and applies a SQL Server database project (`.sqlproj`) to a database, with DacFx. `dacdeploy plan` shows what
applying would change and can save that plan for review. `dacdeploy apply` then applies exactly the plan you reviewed,
and refuses it if the database or the project changed since.

On top of what a plain DacFx publish does:

- **Saved plans are checked.** A saved plan holds the SQL and a fingerprint of DacFx's deployment plan. `apply` plans
  again and stops before running anything unless the fingerprint matches. A plan edited after it was saved, or made for
  another database, is refused.
- **Nothing outside the project is dropped by accident.** Objects and columns the database has and the project doesn't
  are kept and listed in every plan. A kept `NOT NULL` column without a default is made nullable, so inserts that leave
  it out keep working. `--prune` drops them, in a plan of its own once nothing else is pending.
- **Data scripts run in the same deployment.** `Data/*.sql` (reference data) runs again whenever the file changes;
  `Migrations/*.sql` (one-offs) runs once. A one-off can split into a part that runs before the schema changes
  (`--# pre`) and one that runs after (`--# post`). Each run is recorded on the database, so the plan lists only the
  scripts still to run.
- **All or nothing.** Schema changes and data scripts run in one transaction: a failure leaves the database as it was.
- **Logins stay out of files.** `dacdeploy login` saves them in your OS credential store (Keychain, Windows Credential
  Manager, Secret Service).

## Install

```sh
dotnet tool install --global dacdeploy
```

Or pin it per repository with a [local tool manifest](https://learn.microsoft.com/dotnet/core/tools/local-tools-how-to-use),
or with [mise](https://mise.jdx.dev): `mise use dotnet:dacdeploy`.

dacdeploy needs .NET 8 or later, and the .NET SDK to build your project.

## Set up a project

Put a `dacdeploy.json` next to your `.sqlproj`, naming the databases you deploy to:

```json
{
  "Environments": {
    "prod": { "Server": "db.example.com", "Database": "Shop" },
    "test": { "Server": "db-test.example.com", "Database": "ShopTest", "Variables": { "OtherDb": "OtherTest" } }
  }
}
```

dacdeploy works on the `dacdeploy.json` in the current folder or the nearest folder above it. It also reads:

| Setting          | Default                                  |
| ---------------- | ---------------------------------------- |
| `Project`        | the one `.sqlproj` next to dacdeploy.json |
| `PublishProfile` | the project's name with `.publish.xml`    |
| `UserSecretsId`  | none                                     |

Paths are relative to `dacdeploy.json`. An environment's `Variables` are the SQLCMD variables to deploy with.

The publish profile holds how the project deploys. dacdeploy adds its own deployment contributors to it, and needs it
to set `IncludeTransactionalScripts`, so data scripts run in the deployment's transaction:

```xml
<Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <IncludeTransactionalScripts>True</IncludeTransactionalScripts>
  </PropertyGroup>
</Project>
```

Data scripts live next to the project, in `Data/` and `Migrations/`. Name one-offs after the date they're written,
`20261120-backfill-totals.sql`, so they run in order. An SDK-style project builds every `.sql` file under its folder, so
leave the data scripts out of the build, and the plans you save there too:

```xml
<ItemGroup>
  <Build Remove="Data/**;Migrations/**;**/*.plan.sql" />
  <None Include="Data/**/*.sql;Migrations/**/*.sql" />
</ItemGroup>
```

### Your own environments, and environment variables

Each source overrides the one before: `dacdeploy.json`, then your user secrets (when `dacdeploy.json` names a
`UserSecretsId`), then environment variables prefixed `DACDEPLOY_`. Add a local database to your user secrets:

```sh
dotnet user-secrets set --id <UserSecretsId> "Environments:dev:Server" "localhost,1433"
dotnet user-secrets set --id <UserSecretsId> "Environments:dev:Database" "Shop"
```

`dacdeploy environments` lists every environment, whichever source defines it, with its server, database and
variables, the sources that set it, and whether each of its logins is configured, saved or missing; it never shows a
password.

In CI, configure one with variables: `DACDEPLOY_Environments__ci__Server`, `DACDEPLOY_Environments__ci__User`,
`DACDEPLOY_Environments__ci__Password`.

### Logins

An environment's login comes from its `User` and `Password`, or from your OS credential store:

```sh
dacdeploy login -E prod --read-only   # used by plan and extract
dacdeploy login -E prod               # the login that can write, used by apply
```

Logins are saved per server, so every environment on that server uses them. `plan` and `extract` prefer the read-only
login and fall back to the one that can write.

## Use

```sh
dacdeploy plan -E prod --out release.plan.sql   # build the project, show the plan, save it
dacdeploy apply -E prod release.plan.sql        # apply the reviewed plan
```

Without a plan file, `apply` plans, shows the plan and asks before applying; `--yes` applies without asking.

| Command   | Options                                                                        |
| --------- | ------------------------------------------------------------------------------ |
| `plan`    | `--out <file>`, `--exit-code` (exit 2 when there are changes), `--prune`, `--verbose` |
| `apply`   | `[<plan>]`, `--yes`, `--prune`, `--verbose`                                    |
| `extract` | `--write` (replace the project's `Schema/dbo` with the database's schema), `--verbose` |
| `login`   | `--read-only`                                                                  |
| `environments` | lists the environments, where each is defined, and which logins it has, without connecting |

Every command but `environments` takes `-E <environment>`. `dacdeploy <command> --help` describes each one.

## In tests

The `DacDeploy.Core` package holds what dacdeploy deploys with, for test fixtures that deploy a project the way
dacdeploy does:

```csharp
var deployer = new Deployer(new DeploymentFiles(dacpacPath, profilePath, projectDirectory));
deployer.Deploy(new DeploymentTarget("test", server, database, variables, user, password));
```

## Releases

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org): a `fix:` releases a patch version,
a `feat:` a minor one, and a breaking change (`feat!:`, or a `BREAKING CHANGE:` footer) a major one. To release, run
the CI workflow on `main` from the Actions tab (`gh workflow run ci.yml --ref main`): it tests, then semantic-release
tags the version, publishes both packages to nuget.org and writes the GitHub release.
