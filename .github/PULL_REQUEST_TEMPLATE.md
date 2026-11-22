## Resumen del cambio
- Qué resuelve o agrega este PR:

## Checklist antes de mergear a `main`
- [ ] `dotnet build native/HUDMind.sln -c Release` compila con 0 warnings / 0 errors
- [ ] `dotnet run --project native/HUDMind.Tests/HUDMind.Tests.csproj -c Release` pasa al 100%
- [ ] `dotnet run --project native/HUDMind.Windows.Tests/HUDMind.Windows.Tests.csproj -c Release` pasa al 100%
- [ ] `HUDMind.Native.exe --smoke-startup` termina con código `0`
- [ ] Sin API keys, logs locales ni archivos de `config/` incluidos en el diff
