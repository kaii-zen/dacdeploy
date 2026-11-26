# Agent Instructions

- Use Conventional Commits for commit messages and PR titles: `type(scope): summary`, scope optional, summary short and in present tense.
- A `fix:` releases a patch version and a `feat:` a minor one; `!` after the type, or a `BREAKING CHANGE:` footer, releases a major one. Mark a change breaking only when it is.
- Build with `dotnet build`; run `dotnet test tests/DacDeploy.UnitTests`. The integration tests (`tests/DacDeploy.IntegrationTests`) need Docker and start a SQL Server container: let CI run them unless the change needs them locally.
- Fix analyzer warnings and IDE suggestions instead of suppressing them.
- Keep examples, test data and docs generic: no real company, product, server, database or schema names.
