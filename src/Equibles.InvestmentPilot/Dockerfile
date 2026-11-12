FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY CHANGELOG.md ./
COPY src/ src/
RUN dotnet restore src/Equibles.InvestmentPilot/Equibles.InvestmentPilot.csproj
RUN dotnet publish src/Equibles.InvestmentPilot/Equibles.InvestmentPilot.csproj \
    -c Release -o /app/publish --no-restore

FROM runtime AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["/usr/bin/timeout", "--signal=TERM", "--kill-after=5s", "300s", "dotnet", "Equibles.InvestmentPilot.dll"]
