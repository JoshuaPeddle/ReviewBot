FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["src/ReviewBot.Api/ReviewBot.Api.csproj", "src/ReviewBot.Api/"]
COPY ["src/ReviewBot.Core/ReviewBot.Core.csproj", "src/ReviewBot.Core/"]
COPY ["src/ReviewBot.GitHub/ReviewBot.GitHub.csproj", "src/ReviewBot.GitHub/"]
COPY ["src/ReviewBot.Llm.Anthropic/ReviewBot.Llm.Anthropic.csproj", "src/ReviewBot.Llm.Anthropic/"]
COPY ["src/ReviewBot.Llm.OpenAi/ReviewBot.Llm.OpenAi.csproj", "src/ReviewBot.Llm.OpenAi/"]
COPY ["src/ReviewBot.Persistence/ReviewBot.Persistence.csproj", "src/ReviewBot.Persistence/"]
RUN dotnet restore "src/ReviewBot.Api/ReviewBot.Api.csproj"
COPY . .
WORKDIR "/src/src/ReviewBot.Api"
RUN dotnet build "./ReviewBot.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./ReviewBot.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ReviewBot.Api.dll"]
