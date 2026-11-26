# Contributing

Thanks for contributing! This repo uses Conventional Commits for both commit messages and PR titles, and releases from
them (see [Releases](README.md#releases)).

## Commit and PR title format

Use:

    type(scope): summary

Where `scope` is optional. Examples:

    fix: name the command the way it was run in hints
    docs(readme): clarify the publish profile settings

Common `type` values:

- feat
- fix
- docs
- chore
- refactor
- test
- ci
- build
- perf
- revert

Breaking changes:

- Add `!` after the type/scope, e.g. `feat!: read the user secrets ID from the project file`, or
- Add a `BREAKING CHANGE:` footer in the commit body.

The PR title check enforces this format.

## Local workflow

The .NET SDK version is in `global.json` (mise installs it from there: `mise install`).

    dotnet build
    dotnet test tests/DacDeploy.UnitTests
    dotnet test tests/DacDeploy.IntegrationTests   # needs Docker; starts a SQL Server container
