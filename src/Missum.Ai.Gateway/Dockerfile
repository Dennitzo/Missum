FROM mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Missum.Ai.Contracts/Missum.Ai.Contracts.csproj src/Missum.Ai.Contracts/
COPY src/Missum.Ai.Server.Core/Missum.Ai.Server.Core.csproj src/Missum.Ai.Server.Core/
COPY src/Missum.Ai.Gateway/Missum.Ai.Gateway.csproj src/Missum.Ai.Gateway/
RUN dotnet restore src/Missum.Ai.Gateway/Missum.Ai.Gateway.csproj --runtime linux-x64
COPY src/Missum.Ai.Contracts src/Missum.Ai.Contracts
COPY src/Missum.Ai.Server.Core src/Missum.Ai.Server.Core
COPY src/Missum.Ai.Gateway src/Missum.Ai.Gateway
RUN dotnet publish src/Missum.Ai.Gateway/Missum.Ai.Gateway.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --no-restore \
    --output /out

FROM mcr.microsoft.com/dotnet/aspnet@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94
RUN mkdir -p /app /data/database /data/uploads /data/artifacts/worker /data/logs
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    MISSUM_AI_DATA_DIRECTORY=/data \
    MISSUM_AI_GATEWAY_PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Missum.Ai.Gateway.dll"]
