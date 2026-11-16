# syntax=docker/dockerfile:1
# Chiseled: no shell or package manager, non-root. "extra" keeps ICU + tzdata.
# The stage is named "base" so IDE fast-mode debugging can still target it.
FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute-chiseled-extra AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Api/Api.csproj", "Api/"]
RUN dotnet restore "Api/Api.csproj"
COPY . .
RUN dotnet publish "Api/Api.csproj" -c $BUILD_CONFIGURATION --no-restore -o /app/publish /p:UseAppHost=false

FROM base AS final
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Api.dll"]
