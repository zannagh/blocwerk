FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

COPY Directory.Packages.props Directory.Build.props global.json .stylecop.json ./
COPY src/Blocwerk.Core/Blocwerk.Core.csproj src/Blocwerk.Core/
COPY src/Blocwerk.Authentication/Blocwerk.Authentication.csproj src/Blocwerk.Authentication/
COPY src/Blocwerk.HoldDetection/Blocwerk.HoldDetection.csproj src/Blocwerk.HoldDetection/
COPY src/Blocwerk.Web/Blocwerk.Web.csproj src/Blocwerk.Web/
RUN dotnet restore src/Blocwerk.Web/Blocwerk.Web.csproj

COPY src/ src/
RUN dotnet publish src/Blocwerk.Web/Blocwerk.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:5050
EXPOSE 5050

ENTRYPOINT ["dotnet", "Blocwerk.Web.dll"]
