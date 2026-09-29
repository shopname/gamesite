# Render では render.yaml から自動で使われる。
# 手元でビルドする場合は gamesite/ をビルドコンテキストにする:
#   cd gamesite
#   docker build -f server/Dockerfile -t gamesite .
#   docker run -p 8080:8080 -e ConnectionStrings__Postgres="postgresql://..." gamesite

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish server/GameSite.Server/GameSite.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Data__Path=/data/gamesite.db \
    Hosting__BehindProxy=true \
    TZ=Asia/Tokyo
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "GameSite.Server.dll"]
