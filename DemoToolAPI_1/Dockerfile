# The demo tool API in a container, for docker-compose's egress profile, where its only way out is the
# egress proxy. Build from the repository root:
#   docker build -f DemoToolAPI_1/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .
COPY DemoToolAPI_1/DemoToolAPI_1.csproj DemoToolAPI_1/
RUN dotnet restore DemoToolAPI_1/DemoToolAPI_1.csproj
COPY DemoToolAPI_1/ DemoToolAPI_1/
RUN dotnet publish DemoToolAPI_1/DemoToolAPI_1.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "DemoToolAPI_1.dll"]
