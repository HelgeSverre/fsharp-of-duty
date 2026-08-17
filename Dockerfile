FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/Ironsight.Core/Ironsight.Core.fsproj src/Ironsight.Core/
COPY src/Ironsight.Server/Ironsight.Server.fsproj src/Ironsight.Server/
RUN dotnet restore src/Ironsight.Server/Ironsight.Server.fsproj
COPY src/Ironsight.Core/ src/Ironsight.Core/
COPY src/Ironsight.Server/ src/Ironsight.Server/
COPY website/ website/
RUN dotnet publish src/Ironsight.Server/Ironsight.Server.fsproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Ironsight.Server.dll"]
