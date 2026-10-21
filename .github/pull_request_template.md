**What this changes**

**How it was tested** (`build.ps1` runs the analyzers, unit tests and self-test; say if you added tests)

- [ ] `powershell -ExecutionPolicy Bypass -File build.ps1` passes, or `dotnet build` and `dotnet test` pass
- [ ] No real tokens, keys, e-mail addresses or local paths in code, tests or screenshots
- [ ] A new service is one file in the right `src/DcxMail.Providers.*` project plus one line in `ServiceCatalog`
