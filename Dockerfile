FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY VERSION Directory.Build.props global.json EngineeringIntakeGate.slnx ./
COPY src/IntakeGate.Domain/IntakeGate.Domain.csproj src/IntakeGate.Domain/
COPY src/IntakeGate.Application/IntakeGate.Application.csproj src/IntakeGate.Application/
COPY src/IntakeGate.Infrastructure/IntakeGate.Infrastructure.csproj src/IntakeGate.Infrastructure/
COPY src/IntakeGate.Host/IntakeGate.Host.csproj src/IntakeGate.Host/
RUN dotnet restore src/IntakeGate.Host/IntakeGate.Host.csproj

COPY src/ src/
RUN dotnet publish src/IntakeGate.Host/IntakeGate.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN mkdir -p /app/data /app/config && chown -R app:app /app
USER app
COPY --from=build --chown=app:app /app/publish ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "IntakeGate.Host.dll"]
