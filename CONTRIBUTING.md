# Contributing

Issues and PRs are welcome. Keep changes small, focused, and easy to review.

## Build and test

```powershell
git clone https://github.com/garylumsden/copilocal.git
cd copilocal
dotnet build -c Debug
dotnet test tests/Copilocal.Tests/Copilocal.Tests.csproj
```

Requires the .NET 10 SDK.

## Pull requests

- Keep PRs scoped to one bug fix or feature.
- Make sure `dotnet build -c Debug` and `dotnet test tests/Copilocal.Tests/Copilocal.Tests.csproj` are green before opening a PR.
- Update README or governance docs when behavior changes.
- Describe user-visible changes and any provider/runtime versions used for validation.

## Style notes

- Prefer clear, direct code over clever abstractions.
- Keep provider process and HTTP access behind `IProcessRunner` / `IHttpGateway` for testability.
- Keep JSON handling Native-AOT friendly; use the existing hand-rolled JSON helpers/patterns.
- Avoid persisting provider BYOK environment variables; copilocal should set them only on the child `copilot` process.
